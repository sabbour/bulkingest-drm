using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System.Configuration;
using Microsoft.WindowsAzure.MediaServices.Client;
using bulkingestdrm.dto;
using System.Threading.Tasks;
using bulkingestdrm.shared;
using System;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;
using System.Collections.Generic;
using Microsoft.WindowsAzure.MediaServices.Client.Widevine;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.MediaServices.Client.DynamicEncryption;
using Microsoft.WindowsAzure.MediaServices.Client.FairPlay;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;

namespace bulkingestdrm.functions.webhooks
{
    public static class CreateProtectedStreamingLocatorWebhook
    {
        // Read settings from Environment Variables, which are defined in the Application Settings
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];

        static readonly string _tokenPrimaryVerificationKey = ConfigurationManager.AppSettings["DRMToken_PrimaryVerificationKey"];
        static readonly string _tokenAlternativeVerificationKey = ConfigurationManager.AppSettings["DRMToken_AlternativeVerificationKey"];
        static readonly string _tokenScope = ConfigurationManager.AppSettings["DRMToken_Scope"];
        static readonly string _tokenIssuer = ConfigurationManager.AppSettings["DRMToken_Issuer"];

        // FairPlay specific. See link for details: https://azure.microsoft.com/en-us/documentation/articles/media-services-protect-hls-with-fairplay/
        static readonly string _fairPlayPFXPassword = ConfigurationManager.AppSettings["FairPlay_PFXPassword"];
        static readonly string _fairPlayPFXPath = ConfigurationManager.AppSettings["FairPlay_PFXPath"];
        static readonly string _fairPlayASK = ConfigurationManager.AppSettings["FairPlay_ASK"];
        static readonly string _fairPlayASKId = ConfigurationManager.AppSettings["FairPlay_ASKId"];

        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("CreateProtectedStreamingLocatorWebhook")]
        public static async Task<object> Run([HttpTrigger("post", WebHookType = "genericJson")]HttpRequestMessage req, TraceWriter log)
        {
            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);

            var cpslRequest = await req.Content.ReadAsAsync<CreateProtectedStreamingLocatorRequest>();

            // Sanity checks
            #region Sanity checks
            if (cpslRequest == null || cpslRequest.AssetId == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Invalid create streaming locator request."
                });
            #endregion

            var asset = _context.Assets.Where(a => a.Id == cpslRequest.AssetId).FirstOrDefault();
            if (asset == null)
                return req.CreateResponse(HttpStatusCode.NotFound, new
                {
                    error = $"Asset {cpslRequest.AssetId} doesn't exist."
                });

            // Check if it has locators
            if (asset.Locators.Count != 0 && !cpslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has published locators. You need to remove them first before trying to enable DRM or pass in Overwrite=true in the request."
                });

            // Check if it has keys
            if (asset.ContentKeys.Count != 0 && !cpslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has content keys. Pass in Overwrite=true in the request to remove them and overwrite."
                });

            // Check if it has delivery policies
            if (asset.DeliveryPolicies.Count != 0 && !cpslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has delivery policies keys. Pass in Overwrite=true in the request to remove them and overwrite."
                });

            // Now that we're here, let's clean up the asset's locators, keys and delivery policies
            CleanUpAsset(asset);

            // Create a CENC Key and associate it with it
            var CENCKey = CreateContentKey(asset, ContentKeyType.CommonEncryption);

            // Create a CBC Key for FairPlay and associate it with it
            //var CBCKey = CreateContentKey(asset, ContentKeyType.CommonEncryptionCbcs);

            // Add token restriction for PlayReady and Widevine
            var playReadyandWidevineTokenTemplateString = AddPlayReadyAndWidevineTokenRestrictedAuthorizationPolicy(CENCKey);

            // Add token restriction for FairPlay
            //var fairplayTokenTemplateString = AddFairPlayTokenRestrictedAuthorizationPolicyFairPlay(CBCKey);

            // Create Asset Delivery Policy for PlayReady and Widevine
            CreatePlayReadyAndWidevineAssetDeliveryPolicy(asset, CENCKey);

            // Create Asset Delivery Policy for Fairplay
            //CreateFairPlayAssetDeliveryPolicy(asset, CBCKey);

            // Publish and get a locator
            var policy = _context.AccessPolicies.ToList().FirstOrDefault(p => p.Name == "Streaming policy");
            if (policy == null)
                policy = await _context.AccessPolicies.CreateAsync("Streaming policy", TimeSpan.FromDays(30), AccessPermissions.Read);

            // Create a locator to the streaming content on an origin. 
            var originLocator = await _context.Locators.CreateLocatorAsync(LocatorType.OnDemandOrigin, asset, policy, DateTime.UtcNow.AddMinutes(-5));

            // Return the details
            var cpslResponse = new CreateProtectedStreamingLocatorResponse
            {
                AssetId = asset.Id,
                AlternateId = asset.AlternateId,
                CENCKeyId = CENCKey.Id,
                CBCKeyId = "",
                SmoothStreamingUri = GetStreamingUri(asset, ""),
                HLSUri = GetStreamingUri(asset, "(format=m3u8-aapl)"),
                MpegDashUri = GetStreamingUri(asset, "(format=mpd-time-csf)")
            };

            return req.CreateResponse(HttpStatusCode.Created, cpslResponse);
        }

        /// <summary>
        /// Removes locators, delivery policies and keys associated with an asset
        /// </summary>
        /// <param name="asset"></param>
        private static void CleanUpAsset(IAsset asset)
        {
            foreach (var locator in asset.Locators)
            {
                ILocator locatorRefreshed = _context.Locators.Where(p => p.Id == locator.Id).FirstOrDefault();
                if (locatorRefreshed != null)
                {
                    locatorRefreshed.Delete();
                }
            }

            var deliveryPolicies = asset.DeliveryPolicies.ToList();
            foreach (var deliveryPolicy in deliveryPolicies)
            {
                asset.DeliveryPolicies.Remove(deliveryPolicy);
                var deliveryPolicyRefreshed = _context.AssetDeliveryPolicies.Where(p => p.Id == deliveryPolicy.Id).FirstOrDefault();
                if (deliveryPolicyRefreshed != null)
                {
                    deliveryPolicyRefreshed.Delete();
                }
            }

            var keys = asset.ContentKeys.ToList();
            foreach (var key in keys)
            {
                asset.ContentKeys.Remove(key);
                IContentKey keyRefreshed = _context.ContentKeys.Where(p => p.Id == key.Id).FirstOrDefault();
                if (keyRefreshed != null)
                {
                    keyRefreshed.Delete();
                }
            }
        }

        /// <summary>
        /// Create and associate a key
        /// </summary>
        /// <param name="asset"></param>
        /// <returns></returns>
        private static IContentKey CreateContentKey(IAsset asset, ContentKeyType contentKeyType)
        {
            // Create envelope encryption content key
            Guid keyId = Guid.NewGuid();
            var contentKey = DRMHelper.GetRandomBuffer(16);

            IContentKey key = _context.ContentKeys.Create(keyId, contentKey, $"ContentKey {contentKeyType.ToString()}", contentKeyType);

            // Associate the key with the asset.
            asset.ContentKeys.Add(key);

            return key;
        }

        private static string AddPlayReadyAndWidevineTokenRestrictedAuthorizationPolicy(IContentKey contentKey)
        {
            string tokenTemplateString = DRMHelper.GenerateTokenRequirementsString(_tokenPrimaryVerificationKey, _tokenAlternativeVerificationKey, _tokenScope, _tokenIssuer, true);

            List<ContentKeyAuthorizationPolicyRestriction> restrictions = new List<ContentKeyAuthorizationPolicyRestriction>
            {
                new ContentKeyAuthorizationPolicyRestriction
                {
                    Name = "Playready and Widevine Token Authorization Policy",
                    KeyRestrictionType = (int)ContentKeyRestrictionType.TokenRestricted,
                    Requirements = tokenTemplateString,
                }
            };

            // Configure PlayReady and Widevine license templates.
            string PlayReadyLicenseTemplate = ConfigurePlayReadyLicenseTemplate();
            string WidevineLicenseTemplate = ConfigureWidevineLicenseTemplate();

            IContentKeyAuthorizationPolicyOption PlayReadyPolicy = _context.ContentKeyAuthorizationPolicyOptions.Create("PlayReady token option", ContentKeyDeliveryType.PlayReadyLicense, restrictions, PlayReadyLicenseTemplate);
            IContentKeyAuthorizationPolicyOption WidevinePolicy = _context.ContentKeyAuthorizationPolicyOptions.Create("Widevine token option", ContentKeyDeliveryType.Widevine, restrictions, WidevineLicenseTemplate);
            IContentKeyAuthorizationPolicy contentKeyAuthorizationPolicy = _context.ContentKeyAuthorizationPolicies.CreateAsync("Deliver Common Content Key with token restrictions").Result;

            contentKeyAuthorizationPolicy.Options.Add(PlayReadyPolicy);
            contentKeyAuthorizationPolicy.Options.Add(WidevinePolicy);

            // Associate the content key authorization policy with the content key
            contentKey.AuthorizationPolicyId = contentKeyAuthorizationPolicy.Id;
            contentKey = contentKey.UpdateAsync().Result;

            return tokenTemplateString;
        }

        private static string AddFairPlayTokenRestrictedAuthorizationPolicyFairPlay(IContentKey contentKey)
        {
            string tokenTemplateString = DRMHelper.GenerateTokenRequirementsString(_tokenPrimaryVerificationKey, _tokenAlternativeVerificationKey, _tokenScope, _tokenIssuer, true);

            List<ContentKeyAuthorizationPolicyRestriction> restrictions = new List<ContentKeyAuthorizationPolicyRestriction>
            {
                new ContentKeyAuthorizationPolicyRestriction
                {
                    Name = "FairPlay Token Authorization Policy",
                    KeyRestrictionType = (int)ContentKeyRestrictionType.TokenRestricted,
                    Requirements = tokenTemplateString,
                }
            };

            // Configure FairPlay policy option.
            string FairPlayConfiguration = ConfigureFairPlayPolicyOptions();

            IContentKeyAuthorizationPolicyOption FairPlayPolicy = _context.ContentKeyAuthorizationPolicyOptions.Create("FairPlay token option", ContentKeyDeliveryType.FairPlay, restrictions, FairPlayConfiguration);
            IContentKeyAuthorizationPolicy contentKeyAuthorizationPolicy = _context.ContentKeyAuthorizationPolicies.CreateAsync("Deliver CBC Content Key with token restrictions").Result;

            contentKeyAuthorizationPolicy.Options.Add(FairPlayPolicy);

            // Associate the content key authorization policy with the content key
            contentKey.AuthorizationPolicyId = contentKeyAuthorizationPolicy.Id;
            contentKey = contentKey.UpdateAsync().Result;

            return tokenTemplateString;
        }

        private static string ConfigurePlayReadyLicenseTemplate()
        {
            // The following code configures PlayReady License Template using .NET classes
            // and returns the XML string.

            //The PlayReadyLicenseResponseTemplate class represents the template for the response sent back to the end user. 
            //It contains a field for a custom data string between the license server and the application 
            //(may be useful for custom app logic) as well as a list of one or more license templates.
            PlayReadyLicenseResponseTemplate responseTemplate = new PlayReadyLicenseResponseTemplate();

            // The PlayReadyLicenseTemplate class represents a license template for creating PlayReady licenses
            // to be returned to the end users. 
            //It contains the data on the content key in the license and any rights or restrictions to be 
            //enforced by the PlayReady DRM runtime when using the content key.
            PlayReadyLicenseTemplate licenseTemplate = new PlayReadyLicenseTemplate();
            //Configure whether the license is persistent (saved in persistent storage on the client) 
            //or non-persistent (only held in memory while the player is using the license).  
            licenseTemplate.LicenseType = PlayReadyLicenseType.Nonpersistent;

            // AllowTestDevices controls whether test devices can use the license or not.  
            // If true, the MinimumSecurityLevel property of the license
            // is set to 150.  If false (the default), the MinimumSecurityLevel property of the license is set to 2000.
            licenseTemplate.AllowTestDevices = true;

            // You can also configure the Play Right in the PlayReady license by using the PlayReadyPlayRight class. 
            // It grants the user the ability to playback the content subject to the zero or more restrictions 
            // configured in the license and on the PlayRight itself (for playback specific policy). 
            // Much of the policy on the PlayRight has to do with output restrictions 
            // which control the types of outputs that the content can be played over and 
            // any restrictions that must be put in place when using a given output.
            // For example, if the DigitalVideoOnlyContentRestriction is enabled, 
            //then the DRM runtime will only allow the video to be displayed over digital outputs 
            //(analog video outputs won’t be allowed to pass the content).

            //IMPORTANT: These types of restrictions can be very powerful but can also affect the consumer experience. 
            // If the output protections are configured too restrictive, 
            // the content might be unplayable on some clients. For more information, see the PlayReady Compliance Rules document.

            // For example:
            //licenseTemplate.PlayRight.AgcAndColorStripeRestriction = new AgcAndColorStripeRestriction(1);

            responseTemplate.LicenseTemplates.Add(licenseTemplate);

            return MediaServicesLicenseTemplateSerializer.Serialize(responseTemplate);
        }

        private static string ConfigureWidevineLicenseTemplate()
        {
            var template = new WidevineMessage
            {
                allowed_track_types = AllowedTrackTypes.SD_HD,
                content_key_specs = new[]
                {
                    new ContentKeySpecs
                    {
                        required_output_protection = new RequiredOutputProtection { hdcp = Hdcp.HDCP_NONE},
                        security_level = 1,
                        track_type = "SD"
                    }
                },
                policy_overrides = new
                {
                    can_play = true,
                    can_persist = true,
                    can_renew = false
                }
            };

            string configuration = JsonConvert.SerializeObject(template);
            return configuration;
        }

        private static string ConfigureFairPlayPolicyOptions()
        {
            // For testing you can provide all zeroes for ASK bytes together with the cert from Apple FPS SDK. 
            // However, for production you must use a real ASK from Apple bound to a real prod certificate.
            byte[] askBytes = string.IsNullOrEmpty(_fairPlayASK) ? Guid.NewGuid().ToByteArray() : System.Text.Encoding.UTF8.GetBytes(_fairPlayASK);
            var askId = string.IsNullOrEmpty(_fairPlayASKId) ? Guid.NewGuid() : Guid.Parse(_fairPlayASKId);

            // Key delivery retrieves askKey by askId and uses this key to generate the response.
            IContentKey askKey = _context.ContentKeys.Create(askId, askBytes, "askKey", ContentKeyType.FairPlayASk);

            //Customer password for creating the .pfx file.
            string pfxPassword = _fairPlayPFXPassword;

            // Key delivery retrieves pfxPasswordKey by pfxPasswordId and uses this key to generate the response.
            var pfxPasswordId = Guid.NewGuid();
            byte[] pfxPasswordBytes = System.Text.Encoding.UTF8.GetBytes(pfxPassword);
            IContentKey pfxPasswordKey = _context.ContentKeys.Create(pfxPasswordId, pfxPasswordBytes, "pfxPasswordKey", ContentKeyType.FairPlayPfxPassword);

            // iv - 16 bytes random value, must match the iv in the asset delivery policy.
            byte[] iv = Guid.NewGuid().ToByteArray();

            //Specify the .pfx file created by the customer.
            var appCert = new X509Certificate2(_fairPlayPFXPath, pfxPassword, X509KeyStorageFlags.Exportable);

            string FairPlayConfiguration =
                Microsoft.WindowsAzure.MediaServices.Client.FairPlay.FairPlayConfiguration.CreateSerializedFairPlayOptionConfiguration(
                    appCert,
                    pfxPassword,
                    pfxPasswordId,
                    askId,
                    iv);

            return FairPlayConfiguration;
        }

        private static void CreatePlayReadyAndWidevineAssetDeliveryPolicy(IAsset asset, IContentKey key)
        {
            // Get the PlayReady license service URL.
            Uri acquisitionUrl = key.GetKeyDeliveryUrl(ContentKeyDeliveryType.PlayReadyLicense);

            // GetKeyDeliveryUrl for Widevine attaches the KID to the URL.
            // For example: https://amsaccount1.keydelivery.mediaservices.windows.net/Widevine/?KID=268a6dcb-18c8-4648-8c95-f46429e4927c.  
            // The WidevineBaseLicenseAcquisitionUrl (used below) also tells Dynamaic Encryption 
            // to append /? KID =< keyId > to the end of the url when creating the manifest.
            // As a result Widevine license aquisition URL will have KID appended twice, 
            // so we need to remove the KID that in the URL when we call GetKeyDeliveryUrl.

            Uri widevineUrl = key.GetKeyDeliveryUrl(ContentKeyDeliveryType.Widevine);
            UriBuilder uriBuilder = new UriBuilder(widevineUrl);
            uriBuilder.Query = String.Empty;
            widevineUrl = uriBuilder.Uri;

            Dictionary<AssetDeliveryPolicyConfigurationKey, string> assetDeliveryPolicyConfiguration =
                new Dictionary<AssetDeliveryPolicyConfigurationKey, string>
                {
                    {AssetDeliveryPolicyConfigurationKey.PlayReadyLicenseAcquisitionUrl, acquisitionUrl.ToString()},
                    {AssetDeliveryPolicyConfigurationKey.WidevineBaseLicenseAcquisitionUrl, widevineUrl.ToString()}

                };

            var assetDeliveryPolicy = _context.AssetDeliveryPolicies.Create(
                    "PlayReady and Widevine AssetDeliveryPolicy",
                AssetDeliveryPolicyType.DynamicCommonEncryption,
                AssetDeliveryProtocol.Dash | AssetDeliveryProtocol.SmoothStreaming,
                assetDeliveryPolicyConfiguration);

            // Add AssetDelivery Policy to the asset
            asset.DeliveryPolicies.Add(assetDeliveryPolicy);
        }

        private static void CreateFairPlayAssetDeliveryPolicy(IAsset asset, IContentKey key)
        {
            var kdPolicy = _context.ContentKeyAuthorizationPolicies.Where(p => p.Id == key.AuthorizationPolicyId).Single();
            var kdOption = kdPolicy.Options.Single(o => o.KeyDeliveryType == ContentKeyDeliveryType.FairPlay);

            FairPlayConfiguration configFP = JsonConvert.DeserializeObject<FairPlayConfiguration>(kdOption.KeyDeliveryConfiguration);

            // Get the FairPlay license service URL.
            Uri acquisitionUrl = key.GetKeyDeliveryUrl(ContentKeyDeliveryType.FairPlay);

            Dictionary<AssetDeliveryPolicyConfigurationKey, string> assetDeliveryPolicyConfiguration =
                new Dictionary<AssetDeliveryPolicyConfigurationKey, string>
                {
                        {AssetDeliveryPolicyConfigurationKey.FairPlayLicenseAcquisitionUrl, acquisitionUrl.ToString()},
                        {AssetDeliveryPolicyConfigurationKey.CommonEncryptionIVForCbcs, configFP.ContentEncryptionIV}
                };

            var assetDeliveryPolicy = _context.AssetDeliveryPolicies.Create(
                    "FairPlay AssetDeliveryPolicy",
                AssetDeliveryPolicyType.DynamicCommonEncryptionCbcs,
                AssetDeliveryProtocol.HLS,
                assetDeliveryPolicyConfiguration);

            // Add AssetDelivery Policy to the asset
            asset.DeliveryPolicies.Add(assetDeliveryPolicy);
        }

        private static Uri GetStreamingUri(this IAsset asset, string streamingParameter)
        {
            if (asset == null)
            {
                throw new ArgumentNullException("asset", "The asset cannot be null.");
            }

            Uri smoothStreamingUri = null;
            IAssetFile manifestAssetFile = asset
                .AssetFiles
                .ToList()
                .Where(af => af.Name.EndsWith(".ism", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(); ;

            if (manifestAssetFile != null)
            {
                ILocator originLocator = asset
                    .Locators
                    .ToList()
                    .Where(l => l.Type == LocatorType.OnDemandOrigin)
                    .OrderBy(l => l.ExpirationDateTime)
                    .FirstOrDefault();

                if (originLocator != null)
                {
                    smoothStreamingUri = new Uri(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/{1}/manifest{2}",
                            originLocator.Path.TrimEnd('/'),
                            manifestAssetFile.Name,
                            streamingParameter),
                        UriKind.Absolute);
                }
            }

            return smoothStreamingUri;
        }
    }
}