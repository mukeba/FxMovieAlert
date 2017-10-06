using System;
using System.ComponentModel.DataAnnotations;

namespace FxMovies.FxMoviesDB
{
    /// <summary>
    /// A simple class representing a UserRating
    /// </summary>
    public class User
    {
        public User()
        {
        }

        [Key]
        public string ImdbUserId { get; set; }
        public DateTime? RefreshRequestTime { get; set; }
        public DateTime? LastRefreshRatingsTime { get; set; }
        public bool? LastRefreshSuccess { get; set; }
        public string LastRefreshRatingsResult { get; set; }
        public long RefreshCount { get; set; }
        public DateTime? LastUsageTime { get; set; }
        public long Usages { get; set; }
    }   
}