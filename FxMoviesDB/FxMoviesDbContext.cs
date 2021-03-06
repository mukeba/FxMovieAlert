﻿using System;
using Microsoft.EntityFrameworkCore;

namespace FxMovies.FxMoviesDB
{
    /// <summary>
    /// The entity framework context with a Students DbSet 
    /// </summary>
    public class FxMoviesDbContext : DbContext
    {
        public FxMoviesDbContext(DbContextOptions<FxMoviesDbContext> options)
            : base(options)
        { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserRating>()
                .HasKey(u => new { u.UserId, u.ImdbMovieId });
            modelBuilder.Entity<UserWatchListItem>()
                .HasKey(u => new { u.UserId, u.ImdbMovieId });
            modelBuilder.Entity<VodMovie>()
                .HasKey(m => new { m.Provider, m.ProviderCategory, m.ProviderId });
        }

        public DbSet<Channel> Channels { get; set; }
        public DbSet<MovieEvent> MovieEvents { get; set; }
        public DbSet<UserRating> UserRatings { get; set; }
        public DbSet<UserWatchListItem> UserWatchLists { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<VodMovie> VodMovies { get; set; }
    }

    /// <summary>
    /// A factory to create an instance of the StudentsContext 
    /// </summary>
    public static class FxMoviesDbContextFactory
    {
        public static FxMoviesDbContext Create(string connectionString)
        {
            var optionsBuilder = new DbContextOptionsBuilder<FxMoviesDbContext>();
            optionsBuilder.UseSqlite(connectionString);

            // Ensure that the SQLite database and sechema is created!
            var db = new FxMoviesDbContext(optionsBuilder.Options);
            db.Database.EnsureCreated();

            return db;
        }
    }
}
