using System;
using System.Collections.Generic;
using System.Text;

namespace core.Const
{
    public static class TransactionType
    {
        public const string Purchase = "00";
        public const string BalanceInquiry = "31";
        public const string Void = "02";
        public const string Refund = "20";
    }
}
