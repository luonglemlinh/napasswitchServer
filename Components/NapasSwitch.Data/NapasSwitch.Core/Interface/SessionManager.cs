    using System;
using System.Collections.Generic;
using System.Text;

namespace core.Interface
{
    using core.Models;
    public interface SessionManager
    {
        string GenerateSessionId();
        void StoreSession(SessionContext session);
        SessionContext? RetrieveSession(string sessionId);
        void UpdateSession(SessionContext session);
        void RemoveSession(string sessionId);
    }
}
