﻿using AntShares.Cryptography;
using AntShares.IO;
using AntShares.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AntShares.Core
{
    public abstract class Transaction : ISignable
    {
        public readonly TransactionType Type;
        public TransactionInput[] Inputs;
        public TransactionOutput[] Outputs;
        public byte[][] Scripts;

        private UInt256 _hash = null;
        public UInt256 Hash
        {
            get
            {
                if (_hash == null)
                {
                    _hash = new UInt256(this.ToArray().Sha256().Sha256());
                }
                return _hash;
            }
        }

        private IReadOnlyDictionary<TransactionInput, TransactionOutput> _references;
        public IReadOnlyDictionary<TransactionInput, TransactionOutput> References
        {
            get
            {
                if (_references == null)
                {
                    Dictionary<TransactionInput, TransactionOutput> dictionary = new Dictionary<TransactionInput, TransactionOutput>();
                    foreach (var group in GetAllInputs().GroupBy(p => p.PrevTxId))
                    {
                        Transaction tx = Blockchain.Default.GetTransaction(group.Key);
                        if (tx == null) return null;
                        foreach (var reference in group.Select(p => new
                        {
                            Input = p,
                            Output = tx.Outputs[p.PrevIndex]
                        }))
                        {
                            dictionary.Add(reference.Input, reference.Output);
                        }
                    }
                    _references = dictionary;
                }
                return _references;
            }
        }

        byte[][] ISignable.Scripts
        {
            get
            {
                return this.Scripts;
            }
            set
            {
                this.Scripts = value;
            }
        }

        public virtual Fixed8 SystemFee => Fixed8.Zero;

        protected Transaction(TransactionType type)
        {
            this.Type = type;
        }

        void ISerializable.Deserialize(BinaryReader reader)
        {
            if ((TransactionType)reader.ReadByte() != Type)
                throw new FormatException();
            DeserializeWithoutType(reader);
        }

        protected abstract void DeserializeExclusiveData(BinaryReader reader);

        public static Transaction DeserializeFrom(byte[] value)
        {
            using (MemoryStream ms = new MemoryStream(value, false))
            using (BinaryReader reader = new BinaryReader(ms, Encoding.UTF8))
            {
                return DeserializeFrom(reader);
            }
        }

        internal static Transaction DeserializeFrom(BinaryReader reader)
        {
            TransactionType type = (TransactionType)reader.ReadByte();
            string typeName = string.Format("{0}.{1}", typeof(Transaction).Namespace, type);
            Transaction transaction = typeof(Transaction).Assembly.CreateInstance(typeName) as Transaction;
            if (transaction == null)
                throw new FormatException();
            transaction.DeserializeWithoutType(reader);
            return transaction;
        }

        private void DeserializeWithoutType(BinaryReader reader)
        {
            DeserializeExclusiveData(reader);
            this.Inputs = reader.ReadSerializableArray<TransactionInput>();
            if (GetAllInputs().Distinct().Count() != GetAllInputs().Count())
                throw new FormatException();
            this.Outputs = reader.ReadSerializableArray<TransactionOutput>();
            if (Outputs.Any(p => p.Value == Fixed8.Zero))
                throw new FormatException();
            this.Scripts = reader.ReadBytesArray();
        }

        void ISignable.FromUnsignedArray(byte[] value)
        {
            using (MemoryStream ms = new MemoryStream(value, false))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                if ((TransactionType)reader.ReadByte() != Type)
                    throw new FormatException();
                DeserializeExclusiveData(reader);
                this.Inputs = reader.ReadSerializableArray<TransactionInput>();
                this.Outputs = reader.ReadSerializableArray<TransactionOutput>();
            }
        }

        byte[] ISignable.GetHashForSigning()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write((byte)Type);
                SerializeExclusiveData(writer);
                writer.Write(Inputs);
                writer.Write(Outputs);
                writer.Flush();
                return ms.ToArray().Sha256();
            }
        }

        public virtual UInt160[] GetScriptHashesForVerifying()
        {
            if (References == null) throw new InvalidOperationException();
            return Inputs.Select(p => References[p].ScriptHash).Distinct().OrderBy(p => p).ToArray();
        }

        internal IReadOnlyDictionary<UInt256, TransactionResult> GetTransactionResults()
        {
            if (References == null) return null;
            return References.Values.Select(p => new
            {
                AssetId = p.AssetId,
                Value = p.Value
            }).Concat(Outputs.Select(p => new
            {
                AssetId = p.AssetId,
                Value = -p.Value
            })).GroupBy(p => p.AssetId, (k, g) => new TransactionResult
            {
                AssetId = k,
                Amount = g.Sum(p => p.Value)
            }).Where(p => p.Amount != Fixed8.Zero).ToDictionary(p => p.AssetId);
        }

        protected virtual void OnDeserialized()
        {
        }

        void ISerializable.Serialize(BinaryWriter writer)
        {
            writer.Write((byte)Type);
            SerializeExclusiveData(writer);
            writer.Write(Inputs);
            writer.Write(Outputs);
            writer.Write(Scripts);
        }

        public virtual IEnumerable<TransactionInput> GetAllInputs()
        {
            return Inputs;
        }

        protected abstract void SerializeExclusiveData(BinaryWriter writer);

        byte[] ISignable.ToUnsignedArray()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write((byte)Type);
                SerializeExclusiveData(writer);
                writer.Write(Inputs);
                writer.Write(Outputs);
                writer.Flush();
                return ms.ToArray();
            }
        }

        public virtual VerificationResult Verify()
        {
            VerificationResult result = VerificationResult.OK;
            lock (LocalNode.MemoryPool)
            {
                if (LocalNode.MemoryPool.Values.AsParallel().SelectMany(p => p.GetAllInputs()).Intersect(GetAllInputs().AsParallel()).Count() > 0)
                    result |= VerificationResult.DoubleSpent;
            }
            if (!result.HasFlag(VerificationResult.DoubleSpent))
            {
                if (Blockchain.Default.Ability.HasFlag(BlockchainAbility.UnspentIndexes))
                {
                    if (Blockchain.Default.IsDoubleSpend(this))
                        result |= VerificationResult.DoubleSpent;
                }
                else
                {
                    result |= VerificationResult.Incapable;
                }
            }
            foreach (var group in Outputs.Where(p => p.Value < Fixed8.Zero).GroupBy(p => p.AssetId))
            {
                if (group.Key == Blockchain.AntCoin.Hash || group.Key == Blockchain.AntShare.Hash)
                {
                    result |= VerificationResult.Imbalanced;
                    break;
                }
                RegisterTransaction tx = Blockchain.Default.GetTransaction(group.Key) as RegisterTransaction;
                if (tx == null)
                {
                    result |= VerificationResult.LackOfInformation;
                    continue;
                }
                if (tx.Amount != Fixed8.Zero)
                {
                    result |= VerificationResult.Imbalanced;
                    break;
                }
                if (group.Any(p => p.ScriptHash != tx.Issuer && p.ScriptHash != tx.Admin))
                {
                    result |= VerificationResult.Imbalanced;
                    break;
                }
            }
            IReadOnlyDictionary<UInt256, TransactionResult> results = GetTransactionResults();
            if (results == null)
            {
                result |= VerificationResult.LackOfInformation;
            }
            else
            {
                TransactionResult[] results_destroy = results.Values.Where(p => p.Amount > Fixed8.Zero).ToArray();
                if (results_destroy.Length > 1)
                    result |= VerificationResult.Imbalanced;
                else if (results_destroy.Length == 1 && results_destroy[0].AssetId != Blockchain.AntCoin.Hash)
                    result |= VerificationResult.Imbalanced;
                else if (SystemFee > Fixed8.Zero && (results_destroy.Length == 0 || results_destroy[0].Amount < SystemFee))
                    result |= VerificationResult.Imbalanced;
                TransactionResult[] results_issue = results.Values.Where(p => p.Amount < Fixed8.Zero).ToArray();
                if (Type == TransactionType.GenerationTransaction)
                {
                    if (results_issue.Any(p => p.AssetId != Blockchain.AntCoin.Hash))
                        result |= VerificationResult.Imbalanced;
                }
                else if (Type != TransactionType.IssueTransaction)
                {
                    result |= VerificationResult.Imbalanced;
                }
            }
            result |= this.VerifySignature();
            return result;
        }
    }
}
