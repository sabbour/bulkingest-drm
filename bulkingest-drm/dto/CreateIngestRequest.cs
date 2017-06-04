using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class CreateIngestRequest
    {
        public string AlternateId { get; set; }
        public string AssetName { get; set; }
        public List<string> AssetFiles { get; set; }
    }
}
