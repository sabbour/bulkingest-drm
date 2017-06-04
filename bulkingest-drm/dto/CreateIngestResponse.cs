using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class CreateIngestResponse
    {
        public string AlternateId { get; set; }
        public string AssetName { get; set; }
        public string AssetId { get; set; }
        public string IngestManifestId { get; set; }
        public string IngestManifestBlobStorageUriForUpload { get; set; }
    }
}
