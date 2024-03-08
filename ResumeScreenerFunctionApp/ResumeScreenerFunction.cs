using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using iText.Kernel.Pdf;
using Document = iText.Layout.Document;
using iText.Kernel.Pdf.Canvas.Parser;
using ResumeScreenerFunctionApp.Models;
using Azure;
using Azure.AI.TextAnalytics;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace ResumeScreenerFunctionApp
{
    public class ResumeScreenerFunction
    {
        private readonly ILogger<ResumeScreenerFunction> _logger;
        private readonly IConfiguration _config;

        public ResumeScreenerFunction(ILogger<ResumeScreenerFunction> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        [Function("uploadAndExtractEntities")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
        {
            _logger.LogInformation("Processing PDF...");
            FileUploadAndNLPResponse fileUploadAndNLPResponse = new FileUploadAndNLPResponse();

            // Read the uploaded PDF from the request body
            using (Stream pdfStream = req.Body)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    await pdfStream.CopyToAsync(ms);
                    ms.Position = 0;

                    // Process the PDF
                    using (PdfReader reader = new PdfReader(ms))
                    {
                        using (PdfDocument pdfDoc = new PdfDocument(reader))
                        {
                            List<ResumeEntitiesResponse> listOfResumeEntitiesResponse = new List<ResumeEntitiesResponse>();
                            int score = 0;
                            if (pdfDoc.GetNumberOfPages() > 3)
                            {
                                score += 20;
                            }

                            bool isExperienceIncluded = false;
                            bool isNameEmailPhoneNumberIncluded = false;
                            bool isPersonTypeIncluded = false;
                            bool isOrganizationIncluded = false;
                            bool isLocationIncluded = false;

                            for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
                            {
                                ResumeEntitiesResponse resumeEntitiesResponse = await GetResumeEntities(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i)), isPersonTypeIncluded, isOrganizationIncluded, isLocationIncluded);
                                if ((resumeEntitiesResponse.ExperienceRanges.Count > 0 || resumeEntitiesResponse.Experiences.Count > 0) && !isExperienceIncluded)
                                {
                                    score += 20;
                                    isExperienceIncluded = true;
                                }
                                if (!string.IsNullOrEmpty(resumeEntitiesResponse.CandidateName) && !string.IsNullOrEmpty(resumeEntitiesResponse.CandidatePhoneNumber) && !string.IsNullOrEmpty(resumeEntitiesResponse.CandidateEmail) && !isNameEmailPhoneNumberIncluded)
                                {
                                    score += 20;
                                    isNameEmailPhoneNumberIncluded = true;
                                }
                                resumeEntitiesResponse.ResumeScore += score;

                                if (listOfResumeEntitiesResponse.Count() == 0)
                                {
                                    listOfResumeEntitiesResponse.Add(resumeEntitiesResponse);
                                    listOfResumeEntitiesResponse[0].ResumeScore = resumeEntitiesResponse.ResumeScore;
                                }
                                else
                                {
                                    foreach (string skill in resumeEntitiesResponse.Skills)
                                    {
                                        if (!skill.Equals(listOfResumeEntitiesResponse[0].Skills.Any()))
                                        {
                                            listOfResumeEntitiesResponse[0].Skills.Add(skill);
                                        }
                                    }
                                    foreach (string experience in resumeEntitiesResponse.Experiences)
                                    {
                                        if (!experience.Equals(listOfResumeEntitiesResponse[0].Experiences.Any()))
                                        {
                                            listOfResumeEntitiesResponse[0].Experiences.Add(experience);
                                        }
                                    }

                                    foreach (string experienceRange in resumeEntitiesResponse.ExperienceRanges)
                                    {
                                        if (!experienceRange.Equals(listOfResumeEntitiesResponse[0].ExperienceRanges.Any()))
                                        {
                                            listOfResumeEntitiesResponse[0].ExperienceRanges.Add(experienceRange);
                                        }
                                    }
                                    listOfResumeEntitiesResponse[0].ResumeScore = resumeEntitiesResponse.ResumeScore;
                                }
                            }

                            if (listOfResumeEntitiesResponse != null && listOfResumeEntitiesResponse[0] != null)
                            {
                                fileUploadAndNLPResponse.Skills = listOfResumeEntitiesResponse[0].Skills;
                                fileUploadAndNLPResponse.CandidateName = listOfResumeEntitiesResponse[0].CandidateName;
                                fileUploadAndNLPResponse.CandidatePhoneNumber = listOfResumeEntitiesResponse[0].CandidatePhoneNumber;
                                fileUploadAndNLPResponse.CandidateEmail = listOfResumeEntitiesResponse[0].CandidateEmail;
                                fileUploadAndNLPResponse.Experiences = listOfResumeEntitiesResponse[0].Experiences?.Count > 0 ? listOfResumeEntitiesResponse[0].Experiences[0] : null;
                                fileUploadAndNLPResponse.ExperienceRanges = listOfResumeEntitiesResponse[0].ExperienceRanges;
                                fileUploadAndNLPResponse.ResumeScore = listOfResumeEntitiesResponse[0].ResumeScore;
                            }
                            if (listOfResumeEntitiesResponse != null && !string.IsNullOrEmpty(listOfResumeEntitiesResponse[0]?.CandidatePhoneNumber))
                            {
                                await UploadResume(pdfStream, fileUploadAndNLPResponse, listOfResumeEntitiesResponse, "pdf");
                            }
                            // Example: Extract text from the PDF and log it
                            string text = PdfTextExtractor.GetTextFromPage(pdfDoc.GetFirstPage());
                            _logger.LogInformation($"Extracted text: {text}");
                        }
                    }
                }
            }

            return new OkObjectResult(fileUploadAndNLPResponse);
        }

        private async Task UploadResume(Stream pdfStream, FileUploadAndNLPResponse fileUploadAndNLPResponse, List<ResumeEntitiesResponse> listOfResumeEntitiesResponse, string fileType)
        {
            string _connectionString = _config.GetConnectionString("AzureBlobStorageConnectionString");
            // Create a BlobServiceClient object which will be used to create a container client
            BlobServiceClient blobServiceClient = new BlobServiceClient(_connectionString);

            // Create the container and return a container client object
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient("aiblob");
            string? fileName = !string.IsNullOrEmpty(listOfResumeEntitiesResponse[0]?.CandidateName) ? listOfResumeEntitiesResponse[0].CandidateName : "No Candidate Name";
            string filePath = listOfResumeEntitiesResponse[0].CandidatePhoneNumber + "/" + fileName + "." + fileType;
            // Get a reference to a blob
            BlobClient blobClient = containerClient.GetBlobClient(filePath);

            // Upload file to blob storage

            await blobClient.UploadAsync(pdfStream, true);
            fileUploadAndNLPResponse.FilePath = filePath;

            //using (MemoryStream memoryStream = new MemoryStream())
            //{
            //    await pdfStream.CopyToAsync(memoryStream);
            //    memoryStream.Position = 0;
            //    await blobClient.UploadAsync(memoryStream, true);
            //    fileUploadAndNLPResponse.FilePath = filePath;
            //}
        }

        private static async Task<ResumeEntitiesResponse> GetResumeEntities(string document, bool isPersonTypeIncluded, bool isOrganizationIncluded, bool isLocationIncluded)
        {
            ResumeEntitiesResponse resumeEntitiesResponse = new ResumeEntitiesResponse();
            var endpoint = "https://myfirstlangservice1.cognitiveservices.azure.com/";
            var apiKey = "2b5c4439bdb24cdb8fd15cb317fea5c3";

            var client = new TextAnalyticsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            string preprocessedText = document;
            try
            {
                Response<CategorizedEntityCollection> result = await client.RecognizeEntitiesAsync(preprocessedText, language: "en");
                // Post-process entities: Map back to original custom entities
                foreach (var entity in result.Value)
                {
                    string originalEntityText = entity.Text;

                    Console.WriteLine($"\t\tEntity: {originalEntityText}\tType: {entity.Category}\tSubcategory: {entity.SubCategory}");
                    Console.WriteLine($"\t\tScore: {entity.ConfidenceScore:F2}\tLength: {entity.Length}\tOffset: {entity.Offset}\n");

                    switch (entity.Category.ToString())
                    {
                        case "Person":
                            resumeEntitiesResponse.CandidateName = entity.Text;
                            break;
                        case "PersonType":
                            if ((entity.Text.Contains("Certified") || entity.Text.Contains("certified")) && !isPersonTypeIncluded)
                            {
                                resumeEntitiesResponse.ResumeScore += 20;
                                isPersonTypeIncluded = true;
                            }
                            break;
                        case "Organization":
                            if (!isOrganizationIncluded)
                            {
                                resumeEntitiesResponse.ResumeScore += 10;
                                isOrganizationIncluded = true;
                            }
                            break;
                        case "Location":
                            if (!isLocationIncluded)
                            {
                                resumeEntitiesResponse.ResumeScore += 10;
                                isLocationIncluded = true;
                            }
                            break;
                        case "Email":
                            resumeEntitiesResponse.CandidateEmail = entity.Text;
                            break;
                        case "PhoneNumber":
                            resumeEntitiesResponse.CandidatePhoneNumber = entity.Text;
                            break;
                        case "Skill":
                        case "Product":
                            resumeEntitiesResponse.Skills.Add(entity.Text);
                            break;
                        case "DateTime":
                            if (entity.SubCategory == "Duration")
                                resumeEntitiesResponse.Experiences.Add(entity.Text);
                            else if (entity.SubCategory == "DateRange")
                                resumeEntitiesResponse.ExperienceRanges.Add(entity.Text);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (RequestFailedException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            return resumeEntitiesResponse;
        }
    }
}
