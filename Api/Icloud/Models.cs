using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace iCloudApi
{
    // ============ SESSION MODELS ============

    public class iCloudSession
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("auth")]
        public AuthInfo Auth { get; set; } = new AuthInfo();

        [JsonProperty("twoFactorAuthentication")]
        public bool TwoFactorAuthentication { get; set; }

        [JsonProperty("securityCode")]
        public string SecurityCode { get; set; }

        [JsonProperty("clientSettings")]
        public ClientSettings ClientSettings { get; set; } = new ClientSettings();

        [JsonProperty("clientId")]
        public string ClientId { get; set; }

        [JsonProperty("push")]
        public PushInfo Push { get; set; } = new PushInfo();

        [JsonProperty("account")]
        public AccountInfo Account { get; set; }

        [JsonProperty("logins")]
        public List<long> Logins { get; set; } = new List<long>();
    }

    public class AuthInfo
    {
        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("xAppleTwosvTrustToken")]
        public string XAppleTwosvTrustToken { get; set; }

        [JsonProperty("cookies")]
        public List<Dictionary<string, string>> Cookies { get; set; } = new List<Dictionary<string, string>>();

        [JsonProperty("created")]
        public long? Created { get; set; }
    }

    public class ClientSettings
    {
        [JsonProperty("language")]
        public string Language { get; set; } = "en-us";

        [JsonProperty("locale")]
        public string Locale { get; set; } = "en_US";

        [JsonProperty("xAppleWidgetKey")]
        public string XAppleWidgetKey { get; set; } = "83545bf919730e51dbfba24e7e8a78d2";

        [JsonProperty("xAppleIFDClientInfo")]
        public object XAppleIFDClientInfo => new
        {
            U = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/603.3.1 (KHTML, like Gecko) Version/10.1.2 Safari/603.3.1",
            L = Locale,
            Z = "GMT+02:00",
            V = "1.1",
            F = ""
        };

        [JsonProperty("timezone")]
        public string Timezone { get; set; } = "US/Pacific";

        [JsonProperty("clientBuildNumber")]
        public string ClientBuildNumber { get; set; } = "2018Project35";

        [JsonProperty("clientMasteringNumber")]
        public string ClientMasteringNumber { get; set; } = "2018B29";

        [JsonProperty("xAppleIDSessionId")]
        public string XAppleIDSessionId { get; set; }

        [JsonProperty("scnt")]
        public string Scnt { get; set; }
    }

    public class PushInfo
    {
        [JsonProperty("topics")]
        public List<string> Topics { get; set; } = new List<string>();

        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("ttl")]
        public int Ttl { get; set; } = 43200;

        [JsonProperty("courierUrl")]
        public string CourierUrl { get; set; }

        [JsonProperty("registered")]
        public List<string> Registered { get; set; } = new List<string>();
    }

    // ============ ACCOUNT MODELS ============

    public class AccountInfo
    {
        [JsonProperty("dsInfo")]
        public DsInfo DsInfo { get; set; }

        [JsonProperty("webservices")]
        public WebservicesInfo Webservices { get; set; }

        [JsonProperty("hasMinimumDeviceForPhotosWeb")]
        public bool HasMinimumDeviceForPhotosWeb { get; set; }

        [JsonProperty("iCDPEnabled")]
        public bool ICDPEnabled { get; set; }

        [JsonProperty("appsOrder")]
        public List<string> AppsOrder { get; set; }

        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("isExtendedLogin")]
        public bool IsExtendedLogin { get; set; }

        [JsonProperty("pcsEnabled")]
        public bool PcsEnabled { get; set; }

        [JsonProperty("hsaTrustedBrowser")]
        public bool HsaTrustedBrowser { get; set; }
    }

    public class DsInfo
    {
        [JsonProperty("lastName")]
        public string LastName { get; set; }

        [JsonProperty("iCDPEnabled")]
        public bool ICDPEnabled { get; set; }

        [JsonProperty("dsid")]
        public string Dsid { get; set; }

        [JsonProperty("hsaEnabled")]
        public bool HsaEnabled { get; set; }

        [JsonProperty("ironcadeMigrated")]
        public bool IroncadeMigrated { get; set; }

        [JsonProperty("locale")]
        public string Locale { get; set; }

        [JsonProperty("brZoneConsolidated")]
        public bool BrZoneConsolidated { get; set; }

        [JsonProperty("isManagedAppleID")]
        public bool IsManagedAppleID { get; set; }

        [JsonProperty("gilligan_invited")]
        public string GilliganInvited { get; set; }

        [JsonProperty("appleIdAliases")]
        public List<string> AppleIdAliases { get; set; }

        [JsonProperty("hsaVersion")]
        public int HsaVersion { get; set; }

        [JsonProperty("isPaidDeveloper")]
        public bool IsPaidDeveloper { get; set; }

        [JsonProperty("countryCode")]
        public string CountryCode { get; set; }

        [JsonProperty("notificationId")]
        public string NotificationId { get; set; }

        [JsonProperty("primaryEmailVerified")]
        public bool PrimaryEmailVerified { get; set; }

        [JsonProperty("fullName")]
        public string FullName { get; set; }

        [JsonProperty("languageCode")]
        public string LanguageCode { get; set; }

        [JsonProperty("appleId")]
        public string AppleId { get; set; }

        [JsonProperty("firstName")]
        public string FirstName { get; set; }

        [JsonProperty("iCloudAppleIdAlias")]
        public string ICloudAppleIdAlias { get; set; }

        [JsonProperty("notesMigrated")]
        public bool NotesMigrated { get; set; }

        [JsonProperty("hasICloudQualifyingDevice")]
        public bool HasICloudQualifyingDevice { get; set; }

        [JsonProperty("primaryEmail")]
        public string PrimaryEmail { get; set; }

        [JsonProperty("aDsID")]
        public string ADsID { get; set; }

        [JsonProperty("locked")]
        public bool Locked { get; set; }

        [JsonProperty("isHideMyEmailSubscriptionActive")]
        public bool IsHideMyEmailSubscriptionActive { get; set; }

        [JsonProperty("gilligan_enabled")]
        public string GilliganEnabled { get; set; }

        [JsonProperty("isCustomDomainsFeatureAvailable")]
        public bool IsCustomDomainsFeatureAvailable { get; set; }

        [JsonProperty("statusCode")]
        public int StatusCode { get; set; }
    }

    public class WebservicesInfo
    {
        [JsonProperty("reminders")]
        public WebserviceInfo Reminders { get; set; }

        [JsonProperty("notes")]
        public WebserviceInfo Notes { get; set; }

        [JsonProperty("mail")]
        public WebserviceInfo Mail { get; set; }

        [JsonProperty("mailws")]
        public WebserviceInfo Mailws { get; set; }

        [JsonProperty("ckdatabasews")]
        public WebserviceInfo Ckdatabasews { get; set; }

        [JsonProperty("photosupload")]
        public WebserviceInfo Photosupload { get; set; }

        [JsonProperty("photos")]
        public WebserviceInfo Photos { get; set; }

        [JsonProperty("drivews")]
        public WebserviceInfo Drivews { get; set; }

        [JsonProperty("uploadimagews")]
        public WebserviceInfo Uploadimagews { get; set; }

        [JsonProperty("schoolwork")]
        public WebserviceInfo Schoolwork { get; set; }

        [JsonProperty("cksharews")]
        public WebserviceInfo Cksharews { get; set; }

        [JsonProperty("findme")]
        public WebserviceInfo Findme { get; set; }

        [JsonProperty("ckdeviceservice")]
        public WebserviceInfo Ckdeviceservice { get; set; }

        [JsonProperty("iworkthumbnailws")]
        public WebserviceInfo Iworkthumbnailws { get; set; }

        [JsonProperty("calendar")]
        public WebserviceInfo Calendar { get; set; }

        [JsonProperty("docws")]
        public WebserviceInfo Docws { get; set; }

        [JsonProperty("settings")]
        public WebserviceInfo Settings { get; set; }

        [JsonProperty("ubiquity")]
        public WebserviceInfo Ubiquity { get; set; }

        [JsonProperty("streams")]
        public WebserviceInfo Streams { get; set; }

        [JsonProperty("keyvalue")]
        public WebserviceInfo Keyvalue { get; set; }

        [JsonProperty("archivews")]
        public WebserviceInfo Archivews { get; set; }

        [JsonProperty("push")]
        public WebserviceInfo Push { get; set; }

        [JsonProperty("iwmb")]
        public WebserviceInfo Iwmb { get; set; }

        [JsonProperty("iworkexportws")]
        public WebserviceInfo Iworkexportws { get; set; }

        [JsonProperty("geows")]
        public WebserviceInfo Geows { get; set; }

        [JsonProperty("account")]
        public WebserviceInfo Account { get; set; }

        [JsonProperty("fmf")]
        public WebserviceInfo Fmf { get; set; }

        [JsonProperty("fmip")]
        public WebserviceInfo Fmip { get; set; }

        [JsonProperty("contacts")]
        public WebserviceInfo Contacts { get; set; }
    }

    public class WebserviceInfo
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("pcsRequired")]
        public bool PcsRequired { get; set; }

        [JsonProperty("uploadUrl")]
        public string UploadUrl { get; set; }
    }

    // ============ AUTH MODELS ============

    public class AuthTokenResponse
    {
        public string Token { get; set; }
        public string SessionId { get; set; }
        public string Scnt { get; set; }
        public string AuthType { get; set; }
    }

    // ============ MAIL MODELS ============

    public class MailFolder
    {
        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("parent")]
        public string Parent { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("folderType")]
        public string FolderType { get; set; }

        [JsonProperty("children")]
        public List<MailFolder> Children { get; set; }
    }

    public class MailMessage
    {
        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("uid")]
        public int Uid { get; set; }

        [JsonProperty("folder")]
        public string Folder { get; set; }

        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public List<string> To { get; set; }

        [JsonProperty("subject")]
        public string Subject { get; set; }

        [JsonProperty("date")]
        public long Date { get; set; }

        [JsonProperty("size")]
        public int Size { get; set; }

        [JsonProperty("flags")]
        public List<string> Flags { get; set; }

        [JsonProperty("parts")]
        public List<MailMessagePart> Parts { get; set; }

        [JsonProperty("snippet")]
        public string Snippet { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class MailMessagePart
    {
        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("size")]
        public int Size { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    public class MailMessageDetail
    {
        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("to")]
        public List<string> To { get; set; }

        [JsonProperty("subject")]
        public string Subject { get; set; }

        [JsonProperty("date")]
        public long Date { get; set; }

        [JsonProperty("parts")]
        public List<MailMessagePart> Parts { get; set; }

        [JsonProperty("data")]
        public string Data { get; set; }

        [JsonProperty("recordType")]
        public string RecordType { get; set; }
    }

    public class MailMessageList
    {
        public MailMessageListMeta Meta { get; set; }
        public List<MailMessage> Messages { get; set; }
    }

    public class MailMessageListMeta
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    public class MailOutgoingMessage
    {
        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("to")]
        public string To { get; set; }

        [JsonProperty("subject")]
        public string Subject { get; set; }

        [JsonProperty("body")]
        public string Body { get; set; }

        [JsonProperty("from")]
        public string From { get; set; }

        [JsonProperty("textBody")]
        public string TextBody { get; set; }

        [JsonProperty("webmailClientBuild")]
        public string WebmailClientBuild { get; set; }

        [JsonProperty("attachments")]
        public List<object> Attachments { get; set; }

        [JsonProperty("draftGuid")]
        public string DraftGuid { get; set; }
    }

    public class MailPreference
    {
        [JsonProperty("emails")]
        public List<MailEmail> Emails { get; set; }

        [JsonProperty("fullName")]
        public string FullName { get; set; }
    }

    public class MailEmail
    {
        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("canSendFrom")]
        public bool CanSendFrom { get; set; }
    }
}