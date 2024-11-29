using Microsoft.AspNetCore.Http;

namespace BackEnd.ViewModels
{
    public class ChunkUploadModel
    {
        public IFormFile File { get; set; }
        public string UploadId { get; set; }
        public string FileName { get; set; }
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public bool IsLastChunk { get; set; }
        public string Caption { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
    }
}
