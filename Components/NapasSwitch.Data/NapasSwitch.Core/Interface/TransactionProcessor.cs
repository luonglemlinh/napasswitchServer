using System;
using System.Collections.Generic;
using System.Text;

namespace core.Interface
{
    using core.Models;
    public interface TransactionProcessor
    {
        Task<Transaction> ProcessTransactionAsync(SessionContext context);
        Task<Transaction> ProcessVoidAsync(SessionContext originalcontext);
    }
}
