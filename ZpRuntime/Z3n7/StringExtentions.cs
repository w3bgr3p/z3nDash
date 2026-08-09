// Перенесено из z3n7/MethodExtensions/StringExtentions.cs. Копии дословные.
//
// ЧАСТИЧНО: здесь только три метода, которые дублировали наши —
// ToBase64, FromBase64, ParseJwt. Остальные ~380 строк эталонного файла
// (StringToHex, HexToString, JsonToDic, ConvertUrl, Range, CleanFilePath,
// GetFileNameFromUrl, EscapeMarkdown, NewPassword, ProjectExtensions.ToJson)
// дублей не создают и переносятся отдельно.
//
// Класс объявлен partial — как в эталоне, чтобы остаток лёг в тот же тип.

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace z3n7
{
    public static partial class StringExtensions
    {
        public static string ToBase64(this string cookiesJson)
        {
            if (string.IsNullOrEmpty(cookiesJson))
                return string.Empty;

            byte[] bytes = Encoding.UTF8.GetBytes(cookiesJson);
            return Convert.ToBase64String(bytes);
        }

        // Отличие от нашей версии: неверный base64 возвращается как есть, а не
        // приводит к исключению. На этом молча держится разбор jVars, где строка
        // может быть уже расшифрованным JSON.
        public static string FromBase64(this string base64Cookies)
        {
            if (string.IsNullOrEmpty(base64Cookies))
                return string.Empty;

            try
            {
                byte[] bytes = Convert.FromBase64String(base64Cookies);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return base64Cookies;
            }
        }

        public static Dictionary<string, object> ParseJwt(this string jwt)
        {
            var result = new Dictionary<string, object>();

            if (string.IsNullOrEmpty(jwt))
            {
                result["error"] = "Empty token";
                return result;
            }

            var parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                result["error"] = "Invalid JWT format";
                return result;
            }

            try
            {
                // Decode header
                string headerPayload = parts[0].Replace('-', '+').Replace('_', '/');
                switch (headerPayload.Length % 4)
                {
                    case 2: headerPayload += "=="; break;
                    case 3: headerPayload += "="; break;
                }
                var headerJson = Encoding.UTF8.GetString(Convert.FromBase64String(headerPayload));
                var header = JObject.Parse(headerJson);

                // Decode payload
                string payloadB64 = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payloadB64.Length % 4)
                {
                    case 2: payloadB64 += "=="; break;
                    case 3: payloadB64 += "="; break;
                }
                var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(payloadB64));
                var payload = JObject.Parse(payloadJson);

                // Header info
                result["alg"] = header["alg"]?.ToString();
                result["typ"] = header["typ"]?.ToString();
                result["kid"] = header["kid"]?.ToString();

                // Payload info
                result["iss"] = payload["iss"]?.ToString();
                result["sub"] = payload["sub"]?.ToString();
                result["aud"] = payload["aud"]?.ToString();

                // Timestamps
                long iat = payload["iat"]?.Value<long>() ?? 0;
                long exp = payload["exp"]?.Value<long>() ?? 0;

                if (iat > 0)
                {
                    result["iat"] = iat;
                    result["iat_dt"] = DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime;
                }

                if (exp > 0)
                {
                    result["exp"] = exp;
                    result["exp_dt"] = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                    result["ttl_seconds"] = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    result["is_expired"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp;
                }

                // Raw payloads
                result["header_json"] = headerJson;
                result["payload_json"] = payloadJson;
                result["signature"] = parts[2];

                return result;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
                return result;
            }
        }
    }
}
