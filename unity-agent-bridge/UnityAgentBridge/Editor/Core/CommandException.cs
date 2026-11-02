using System;

namespace UnityAgentBridge
{
    /// <summary>Thrown by command handlers to report a user-facing failure.</summary>
    public class CommandException : Exception
    {
        public string Code { get; private set; }

        public CommandException(string message) : base(message)
        {
            Code = "error";
        }

        public CommandException(string message, string code) : base(message)
        {
            Code = code ?? "error";
        }
    }
}
