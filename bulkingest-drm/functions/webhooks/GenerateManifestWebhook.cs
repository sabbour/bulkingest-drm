using bulkingestdrm.dto;
using bulkingestdrm.shared;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.MediaServices.Client;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace bulkingestdrm.functions.webhooks
{
    public static class GenerateManifestWebhook
    {
        // Read settings from Environment Variables, which are defined in the Application Settings
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];

        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("GenerateManifestWebhook")]
        public static async Task<object> Run([HttpTrigger("post", WebHookType = "genericJson")]HttpRequestMessage req, TraceWriter log, ExecutionContext executionContext)
        {
            log.Info("GenerateManifest requested.");

            var gmRequest = await req.Content.ReadAsAsync<GenerateManifestRequest>();

            // Sanity checks
            #region Sanity checks
            if (gmRequest == null || gmRequest.AssetId == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Invalid generate manifest request."
                });
            #endregion


            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);

            // Get the asset
            var asset = _context.Assets.Where(a => a.Id == gmRequest.AssetId).FirstOrDefault();
            if (asset == null)
                return req.CreateResponse(HttpStatusCode.NotFound, new
                {
                    error = $"Asset {gmRequest.AssetId} doesn't exist."
                });

            log.Info("Found the asset. Generating manifest.ism");
            try
            {
                var manifestFilePath = executionContext.FunctionDirectory + @"\..\bin\shared\Manifest.ism";
                var smildata = ManifestHelper.LoadAndUpdateManifestTemplate(asset,manifestFilePath);
                var smilXMLDocument = XDocument.Parse(smildata.Content);
                
                // Check if the manifest file exists
                if(asset.AssetFiles.Where(af=>af.Name == smildata.FileName).FirstOrDefault()!=null)
                {
                    // Do nothing
                    log.Info("Manifest already exists.");
                    return req.CreateResponse(HttpStatusCode.OK, new
                    {
                        message = "Manifest already exists"
                    });
                }

                var smildataAssetFile = asset.AssetFiles.Create(smildata.FileName);

                var stream = new MemoryStream();  // Create a stream
                smilXMLDocument.Save(stream);      // Save XDocument into the stream
                stream.Position = 0;   // Rewind the stream ready to read from it elsewhere
                smildataAssetFile.Upload(stream);

                // Update the asset to set the primary file as the ism file
                ManifestHelper.SetFileAsPrimary(asset, smildata.FileName);
            }
            catch (Exception ex)
            {
                log.Error("Could not generate the manifest.");
                log.Error(ex.ToString());

                return req.CreateResponse(HttpStatusCode.InternalServerError, new
                {
                    error = $"Could not generate the manifest. Are you sure the asset contains media files (mp4, m4a)?."
                });
            }

            // Fetching the name from the path parameter in the request URL
            return req.CreateResponse(HttpStatusCode.Created, new
            {
                message = "Manifest created"
            });
        }
    }
}