using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class MonitorIngestRequest
    {
        public string AssetId { get; set; }
        public string IngestManifestId { get; set; }
    }
}
