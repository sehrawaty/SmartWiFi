using System.Security;
using System.Text;

namespace SmartWiFi.Core.Wifi;

/// <summary>Builds the XML that Windows' WlanSetProfile expects for a personal (password) or open network.</summary>
public static class WlanProfileXml
{
    public static string Build(string ssid, WifiSecurity security, string? password)
    {
        var (auth, enc) = security switch
        {
            WifiSecurity.Open => ("open", "none"),
            WifiSecurity.Wpa3Personal => ("WPA3SAE", "AES"),
            WifiSecurity.Wpa2Personal => ("WPA2PSK", "AES"),
            _ => throw new NotSupportedException($"Can't build a profile for {security} networks; add it in Windows first."),
        };

        if (security != WifiSecurity.Open && string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is needed for a secured network.", nameof(password));

        var name = SecurityElement.Escape(ssid);
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n");
        sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\n");
        sb.Append($"  <name>{name}</name>\n");
        sb.Append($"  <SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>\n");
        sb.Append("  <connectionType>ESS</connectionType>\n");
        sb.Append("  <connectionMode>auto</connectionMode>\n");
        sb.Append("  <MSM><security>\n");
        sb.Append($"    <authEncryption><authentication>{auth}</authentication><encryption>{enc}</encryption><useOneX>false</useOneX></authEncryption>\n");
        if (security != WifiSecurity.Open)
        {
            sb.Append("    <sharedKey><keyType>passPhrase</keyType><protected>false</protected>");
            sb.Append($"<keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>\n");
        }
        sb.Append("  </security></MSM>\n");
        sb.Append("</WLANProfile>\n");
        return sb.ToString();
    }
}
