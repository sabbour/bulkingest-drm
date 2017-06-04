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
    public static class CreateStreamingLocatorWebhook
    {
        // Read settings from Environment Variables, which are defined in the Application Settings
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];


        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("CreateStreamingLocatorWebhook")]
        public static async Task<object> Run([HttpTrigger("post", WebHookType = "genericJson")]HttpRequestMessage req, TraceWriter log)
        {
            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);

            var cslRequest = await req.Content.ReadAsAsync<CreateStreamingLocatorRequest>();

            // Sanity checks
            #region Sanity checks
            if (cslRequest == null || cslRequest.AssetId == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Invalid create streaming locator request."
                });
            #endregion

            var asset = _context.Assets.Where(a => a.Id == cslRequest.AssetId).FirstOrDefault();
            if (asset == null)
                return req.CreateResponse(HttpStatusCode.NotFound, new
                {
                    error = $"Asset {cslRequest.AssetId} doesn't exist."
                });

            // Check if it has locators
            if (asset.Locators.Count != 0 && !cslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has published locators. You need to remove them first before trying to enable DRM or pass in Overwrite=true in the request."
                });

            // Check if it has keys
            if (asset.ContentKeys.Count != 0 && !cslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has content keys. Pass in Overwrite=true in the request to remove them and overwrite."
                });

            // Check if it has delivery policies
            if (asset.DeliveryPolicies.Count != 0 && !cslRequest.Overwrite)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Asset already has delivery policies keys. Pass in Overwrite=true in the request to remove them and overwrite."
                });

            // Now that we're here, let's clean up the asset's locators, keys and delivery policies
            CleanUpAsset(asset);
            // Publish and get a locator
            var policy = _context.AccessPolicies.ToList().FirstOrDefault(p => p.Name == "Streaming policy");
            if (policy == null)
                policy = await _context.AccessPolicies.CreateAsync("Streaming policy", TimeSpan.FromDays(30), AccessPermissions.Read);

            // Create a locator to the streaming content on an origin. 
            var originLocator = await _context.Locators.CreateLocatorAsync(LocatorType.OnDemandOrigin, asset, policy, DateTime.UtcNow.AddMinutes(-5));

            // Return the details
            var cslResponse = new CreateStreamingLocatorResponse
            {
                AssetId = asset.Id,
                AlternateId = asset.AlternateId,
                SmoothStreamingUri = GetStreamingUri(asset, ""),
                IsProtected = false,
                HLSUri = GetStreamingUri(asset, "(format=m3u8-aapl)"),
                MpegDashUri = GetStreamingUri(asset, "(format=mpd-time-csf)")
            };

            return req.CreateResponse(HttpStatusCode.Created, cslResponse);
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