﻿using System;

namespace AntShares.Core
{
    [Flags]
    public enum BlockchainAbility : byte
    {
        None = 0,

        /// <summary>
        /// 必须实现的虚函数：GetNextBlock
        /// </summary>
        BlockIndexes = 0x01,

        /// <summary>
        /// 必须实现的虚函数：GetAssets, GetEnrollments
        /// </summary>
        TransactionIndexes = 0x02,

        /// <summary>
        /// 必须实现的虚函数：ContainsUnspent, GetUnspent, GetUnspentAntShares, GetVotes, IsDoubleSpend
        /// </summary>
        UnspentIndexes = 0x04,

        /// <summary>
        /// 必须实现的虚函数：GetQuantityIssued
        /// </summary>
        Statistics = 0x08,

        All = 0xff
    }
}
