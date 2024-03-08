using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.IO;

namespace ResumeScreenerFunctionApp
{
    public class DownloadFunction
    {
        private readonly ILogger<DownloadFunction> _logger;
        private readonly IConfiguration _config;
        public DownloadFunction(ILogger<DownloadFunction> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        [Function("DownloadResume")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            try
            {
                string fileName;
                using (StreamReader streamReader = new StreamReader(req.Body))
                {
                    fileName = await streamReader.ReadToEndAsync();
                }
                if (!string.IsNullOrEmpty(fileName))
                {
                    CloudBlockBlob blockBlob;
                    await using (MemoryStream memoryStream = new MemoryStream())
                    {
                        string _connectionString = _config.GetConnectionString("AzureBlobStorageConnectionString");
                        CloudStorageAccount cloudStorageAccount = CloudStorageAccount.Parse(_connectionString);
                        CloudBlobClient cloudBlobClient = cloudStorageAccount.CreateCloudBlobClient();
                        CloudBlobContainer cloudBlobContainer = cloudBlobClient.GetContainerReference("aiblob");
                        blockBlob = cloudBlobContainer.GetBlockBlobReference(fileName);
                        await blockBlob.DownloadToStreamAsync(memoryStream);
                        Stream blobStream = blockBlob.OpenReadAsync().Result;
                        return new FileStreamResult(blobStream, blockBlob.Properties.ContentType)
                        {
                            FileDownloadName = blockBlob.Name
                        };
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            return null;
        }
    }
}
