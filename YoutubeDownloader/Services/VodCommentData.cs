using System;

namespace YoutubeDownloader.Services
{
    public class VodCommentData
    {
        private double _offsetSeconds;

        /// <summary>
        /// 时间间隔
        /// </summary>
        public string? TimeInterval { get; set; }
        public double CommentsScore { get; set; }
        public double OffsetSeconds
        {
            get => _offsetSeconds;
            set
            {
                _offsetSeconds = value;
                TimeSpan timeSpan = TimeSpan.FromSeconds(value);
                OffsetInterval = timeSpan.ToString(@"hh\:mm\:ss");
            }
        }
        public string? OffsetInterval { get; set; }
    }
}
