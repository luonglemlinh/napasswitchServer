using System;
using core.Models;
using router;

namespace ReversalVerifier
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== NAPAS Reversal DE #5 Verification ===");

            var stateMachine = new TransactionStateMachine();
            
            // Scenario 1: Basic conversion
            // DE 4: 100.00 (represented as 10000 cents in long format for calculation)
            // DE 9: 71212345 (Rate 0.1212345)
            RunTestCase(stateMachine, "100.00 Conversion", "000000010000", "71212345", "000000001212");

            // Scenario 2: Different decimal places
            // DE 9: 61212345 (Rate 1.212345)
            RunTestCase(stateMachine, "1.21 Conversion", "000000010000", "61212345", "000000012123");

            // Scenario 3: Large amount
            // DE 4: 1,000,000.00 (100,000,000 cents)
            // DE 9: 71212345 (Rate 0.1212345)
            // 100,000,000 * 0.1212345 = 12,123,450
            RunTestCase(stateMachine, "Large Amount", "000000100000000", "71212345", "000012123450");
            
            Console.WriteLine("\n=== Verification Completed ===");
        }

        static void RunTestCase(TransactionStateMachine sm, string name, string de4, string de9, string expectedDe5)
        {
            Console.WriteLine($"\nTest: {name}");
            
            var request = new IsoMessage { MessageType = "0200" };
            request.SetField(4, de4);
            request.SetField(9, de9);
            request.SetField(50, "704"); // VND
            request.SetField(11, "123456");

            var context = sm.CreateTransaction("TEST-SESSION", request);
            var reversal = sm.CreateAutoReversal(context);

            string actualDe5 = reversal.GetField(5) ?? "MISSING";
            string actualDe9 = reversal.GetField(9) ?? "MISSING";
            string actualDe50 = reversal.GetField(50) ?? "MISSING";

            Console.WriteLine($"  DE 4: {de4}");
            Console.WriteLine($"  DE 9: {de9}");
            Console.WriteLine($"  Expected DE 5: {expectedDe5}");
            Console.WriteLine($"  Actual   DE 5: {actualDe5}");
            
            bool success = actualDe5 == expectedDe5 && actualDe9 == de9 && actualDe50 == "704";
            
            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  RESULT: PASS");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  RESULT: FAIL");
            }
            Console.ResetColor();
        }
    }
}
