
using OtpNet;

public class UiTools
{
    public static string GenerateOtp(string keyString, int waitIfTimeLess = 5)
    {
        if (string.IsNullOrEmpty(keyString))
            throw new Exception($"invalid input:[{keyString}]");

        var key = Base32Encoding.ToBytes(keyString.Trim());
        var otp = new Totp(key);
        string code = otp.ComputeTotp();
        int remainingSeconds = otp.RemainingSeconds();
            
        if (remainingSeconds <= waitIfTimeLess)
        {
            Thread.Sleep(remainingSeconds * 1000 + 1);
            code = otp.ComputeTotp();
        }
            
        return code;
    }    
}