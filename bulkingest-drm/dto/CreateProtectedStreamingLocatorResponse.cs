using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class CreateProtectedStreamingLocatorResponse
    {
        public string AssetId { get; set; }
        public string AlternateId { get; set; }

        /// <summary>
        /// Key ID for PlayReady and Widevine
        /// </summary>
        public string CENCKeyId { get; set; }

        /// <summary>
        /// Key ID for FairPlay
        /// </summary>
        public string CBCKeyId { get; set; }

        public Uri SmoothStreamingUri { get; set; }
        public Uri MpegDashUri { get; set; }
        public Uri HLSUri { get; set; }
    }
}
