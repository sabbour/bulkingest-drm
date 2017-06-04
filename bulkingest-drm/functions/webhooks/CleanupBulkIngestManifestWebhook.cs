using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System.Configuration;
using Microsoft.WindowsAzure.MediaServices.Client;
using System.Threading.Tasks;
using bulkingestdrm.dto;

namespace bulkingestdrm.functions.webhooks
{
    public static class CleanupBulkIngestManifestWebhook
    {
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];

        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("CleanupBulkIngestManifestWebhook")]
        public static async Task<object> Run([HttpTrigger(WebHookType = "genericJson")]HttpRequestMessage req, TraceWriter log)
        {
            var ciRequest = await req.Content.ReadAsAsync<CleanupManifestRequest>();

            // Sanity checks
            #region Sanity checks
            if (ciRequest == null || ciRequest.IngestManifestId == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, new
                {
                    error = "Invalid clean bulk ingest manifest request."
                });
            #endregion


            log.Info($"CleanIngest requested for IngestManifest {ciRequest.IngestManifestId}");

            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);

            var ingestManifest = _context.IngestManifests.Where(m => m.Id == ciRequest.IngestManifestId).FirstOrDefault();
            if (ingestManifest == null)
                return req.CreateResponse(HttpStatusCode.OK, new
                {
                    error = $"IngestManifest {ciRequest.IngestManifestId} doesn't exist."
                });


            await ingestManifest.DeleteAsync();
            return req.CreateResponse(HttpStatusCode.OK, new
            {
                message = $"IngestManifest {ciRequest.IngestManifestId} deleted."
            });
        }
    }
}
