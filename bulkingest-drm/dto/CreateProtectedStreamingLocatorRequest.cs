using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bulkingestdrm.dto
{
    public class CreateProtectedStreamingLocatorRequest
    {
        /// <summary>
        /// If set to true, the service will delete and recreate the locators, even if they exist on the asset
        /// </summary>
        public bool Overwrite { get; set; }
        public string AssetId { get; set; }
    }
}
