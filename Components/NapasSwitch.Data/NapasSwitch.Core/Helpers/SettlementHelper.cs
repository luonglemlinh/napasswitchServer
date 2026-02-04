using System;
using core.Models;

namespace core.Helpers
{
    public static class SettlementHelper
    {
        /// <summary>
        /// Calculates and adds DE #5 (Settlement Amount) to an ISO message if DE #4 and DE #9 are present.
        /// Also ensures DE #9 and DE #50 are present as required.
        /// </summary>
        public static void AddSettlementAmount(IsoMessage message)
        {
            if (message.HasField(4) && message.HasField(9))
            {
                try
                {
                    string de4 = message.GetField(4)!;
                    string de9 = message.GetField(9)!;

                    if (de9.Length == 8 && long.TryParse(de4, out long trnAmount))
                    {
                        int decimalPlaces = int.Parse(de9.Substring(0, 1));
                        if (long.TryParse(de9.Substring(1), out long rawRate))
                        {
                            decimal rate = (decimal)(rawRate / Math.Pow(10, decimalPlaces));
                            decimal settlementAmount = trnAmount * rate;
                            
                            // Format DE #5: 12 digits, right indented, zero filled
                            // We use Math.Round to handle any floating point precision issues during conversion
                            message.SetField(5, ((long)Math.Round(settlementAmount)).ToString("D12"));
                            
                            // Ensure DE #50 is present if not already there (defaulting to VND 704 if missing)
                            if (!message.HasField(50))
                            {
                                message.SetField(50, message.GetField(49) ?? "704");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SETTLEMENT-HELPER] Error calculating Settlement Amount: {ex.Message}");
                }
            }
        }
    }
}
