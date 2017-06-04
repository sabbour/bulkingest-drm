using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using bulkingestdrm.dto;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.MediaServices.Client;
using System;
using System.Threading;
using System.Configuration;

namespace bulkingestdrm.functions
{
    public static class CreateIngest
    {
        // Read settings from Environment Variables, which are defined in the Application Settings
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];
        static readonly string _randomId = Guid.NewGuid().ToString();

        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("CreateIngest")]
        
        public static HttpResponseMessage Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "createingest")] HttpRequestMessage req, [Queue("monitoringestqueue", Connection = "WF_StorageAccountConnectionString")] out MonitorIngestRequest monitorIngestMessage ,TraceWriter log)
        {
            log.Info("CreateIngest requested.");
            monitorIngestMessage = null;

            var ciRequest = req.Content.ReadAsAsync<CreateIngestRequest>().Result;

            // Sanity checks
            #region Sanity checks
            if(ciRequest == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, "Invalid create injest request.");
            else
            {
                if (string.IsNullOrWhiteSpace(ciRequest.AssetName))
                    ciRequest.AssetName = $"asset-{_randomId}";
                if(ciRequest.AssetFiles == null || ciRequest.AssetFiles.Count == 0)
                    return req.CreateResponse(HttpStatusCode.BadRequest, "Invalid create injest request. AssetFiles must contain at least one file.");
                else
                {
                    foreach (var assetFile in ciRequest.AssetFiles)
                    {
                        if(string.IsNullOrEmpty(assetFile))
                            return req.CreateResponse(HttpStatusCode.BadRequest, "Invalid create injest request. AssetFiles contains at least one blank entry.");
                    }
                }
            }
            #endregion
            

            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);
            
            // 1. Create Asset
            #region 1. Create Asset
            log.Info($"Creating Asset with Asset Name {ciRequest.AssetName}");
            var asset = _context.Assets.Create(ciRequest.AssetName, AssetCreationOptions.None); // Create asset
            asset.AlternateId = ciRequest.AlternateId;

            log.Info($"Updating Asset with AlternateId {ciRequest.AlternateId}");
            asset.Update();
            #endregion
            
            // 2. Create IngestManifest
            #region 2. Create IngestManifest
            log.Info("Creating IngestManifest.");
            var ingestManifest = _context.IngestManifests.Create($"ingestmanifest-{_randomId}");
            #endregion

            // 3. Add the file names to the IngestManifest as assets
            #region 3. Add the file names to the IngestManifest as assets
            log.Info("Adding file names to IngestManifest.");
            var ingestManifestAsset = ingestManifest.IngestManifestAssets.Create(asset, ciRequest.AssetFiles.ToArray<string>());
            #endregion

            var ciResponse = new CreateIngestResponse
            {
                AlternateId = asset.AlternateId,
                AssetName = asset.Name,
                AssetId = asset.Id,
                IngestManifestId = ingestManifest.Id,
                IngestManifestBlobStorageUriForUpload = ingestManifest.BlobStorageUriForUpload
            };

            // Pass the Asset ID and IngestManifest ID to the queue, to trigger the logic app
            log.Info($"Added Asset ID {asset.Id} and IngestManifest ID {ingestManifest.Id} to queue to trigger the Logic App.");
            monitorIngestMessage = new MonitorIngestRequest { AssetId = asset.Id, IngestManifestId = ingestManifest.Id, IsProtected = ciRequest.IsProtected };

            log.Info("Returning response.");
            return req.CreateResponse(HttpStatusCode.Created, ciResponse);
        }
    }
}