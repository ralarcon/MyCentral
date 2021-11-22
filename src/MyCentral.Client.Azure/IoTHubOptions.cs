using System.ComponentModel.DataAnnotations;

namespace MyCentral.Client.Azure
{
    public class IoTHubOptions
    {
        [Required]
        public string EventHubConnectionString { get; set; } = null!;

        [Required]
        public string HostName { get; set; } = null!;

        [Required]
        public string BlobStorageConnectionString { get; set; } = null!;

        [Required]
        public string BlobContainerName { get; set; } = null!;
    }
}
