using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System.Configuration;
using Microsoft.WindowsAzure.MediaServices.Client;
using System;
using Microsoft.WindowsAzure.MediaServices.Client.ContentKeyAuthorization;
using bulkingestdrm.shared;
using bulkingestdrm.dto;

namespace bulkingestdrm.functions
{
    public static class GetToken
    {
        // Read settings from Environment Variables, which are defined in the Application Settings
        static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["WF_MediaServicesAccountName"];
        static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["WF_MediaServicesAccountKey"];

        static readonly string _tokenPrimaryVerificationKey = ConfigurationManager.AppSettings["DRMToken_PrimaryVerificationKey"];
        static readonly string _tokenAlternativeVerificationKey = ConfigurationManager.AppSettings["DRMToken_AlternativeVerificationKey"];
        static readonly string _tokenScope = ConfigurationManager.AppSettings["DRMToken_Scope"];
        static readonly string _tokenIssuer = ConfigurationManager.AppSettings["DRMToken_Issuer"];

        // Media Services Credentials and Cloud Media Context fields
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        [FunctionName("GetToken")]

        public static HttpResponseMessage Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "token/asset/{assetId}/key/{keyId}")]HttpRequestMessage req, string assetId, string keyId, TraceWriter log)
        {

            // Create and cache the Media Services credentials in a static class variable
            _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName, _mediaServicesAccountKey);

            // Used the cached credentials to create CloudMediaContext
            _context = new CloudMediaContext(_cachedCredentials);
            
            var asset = _context.Assets.Where(a => a.Id == assetId).FirstOrDefault();
            if (asset == null)
                return req.CreateResponse(HttpStatusCode.NotFound, $"Asset {assetId} doesn't exist.");

            // Get the raw key value that we'll need to pass to generate the token bec. we specified TokenClaim.ContentKeyIdentifierClaim in during the creation of TokenRestrictionTemplate. 
            Guid rawkey = EncryptionUtils.GetKeyIdAsGuid(keyId);

            TokenRestrictionTemplate tokenTemplate = DRMHelper.GenerateTokenRequirements(_tokenPrimaryVerificationKey, _tokenAlternativeVerificationKey, _tokenScope, _tokenIssuer, true);


            string testToken = TokenRestrictionTemplateSerializer.GenerateTestToken(
                tokenTemplate,
                new SymmetricVerificationKey(Convert.FromBase64String(_tokenPrimaryVerificationKey)),
                rawkey,
                DateTime.UtcNow.AddDays(365)
            );

            var tokenResponse = new TokenResponse {  Token = testToken,  TokenBase64 = testToken.Base64Encode() };
            return req.CreateResponse(HttpStatusCode.OK, tokenResponse);

        }
    }
}