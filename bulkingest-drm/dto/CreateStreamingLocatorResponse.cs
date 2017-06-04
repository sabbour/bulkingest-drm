using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class CreateStreamingLocatorResponse
    {
        public string AssetId { get; set; }
        public string AlternateId { get; set; }
        public bool IsProtected { get; set; }
        public Uri SmoothStreamingUri { get; set; }
        public Uri MpegDashUri { get; set; }
        public Uri HLSUri { get; set; }
    }
}
