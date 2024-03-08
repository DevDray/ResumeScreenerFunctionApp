using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResumeScreenerFunctionApp.Models
{
    public class FileUploadAndNLPResponse
    {
        public string? FilePath { get; set; }
        public List<string>? Skills { get; set; }
        public string? CandidateName { get; set; }
        public string? CandidateEmail { get; set; }
        public string? CandidatePhoneNumber { get; set; }
        public int ResumeScore { get; set; }
        public string? Experiences { get; set; }
        public List<string>? ExperienceRanges { get; set; }
    }
}
