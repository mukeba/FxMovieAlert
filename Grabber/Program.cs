﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using FxMovies.FxMoviesDB;
using FxMovies.ImdbDB;
using Microsoft.Extensions.Configuration;

// Compile: 

namespace FxMovies.Grabber
{
    class Program
    {
        static void Main(string[] args)
        {
            // nice -n 16 dotnet ./Grabber/bin/Release/netcoreapp2.0/Grabber.dll GenerateImdbDatabase
            // nice -n 16 dotnet ./Grabber/bin/Release/netcoreapp2.0/Grabber.dll UpdateEPG
            string command = null;
            var arguments = new List<string>();
            foreach (var arg in args)
                if (arg.StartsWith('-'))
                {
                    continue;
                }
                else if (command == null)
                {
                    command = arg;
                }
                else
                {
                    arguments.Add(arg);
                }
            
            if (command == null)
            {                
                // Usage
                Help();

                return;
            }

            if (command.Equals("Help", StringComparison.InvariantCultureIgnoreCase))
            {
                Help();
            } 
            else if (command.Equals("GenerateImdbDatabase"))
            {
                if (arguments.Count != 0)
                {
                    Console.WriteLine("GenerateImdbDatabase: Invalid argument count");
                    return;
                }
                ImportImdbData_Movies();
                ImportImdbData_Ratings();
            }
            else if (command.Equals("UpdateEPG"))
            {
                if (arguments.Count != 0)
                {
                    Console.WriteLine("UpdateEPG: Invalid argument count");
                    return;
                }
                UpdateDatabaseEpg();
                UpdateEpgDataWithImdb();
            }
            else if (command.Equals("Manual"))
            {
                if (arguments.Count != 2)
                {
                    Console.WriteLine("Manual: Invalid argument count");
                    return;
                }

                UpdateEpgDataWithImdbManual(int.Parse(arguments[0]), arguments[1]);
            }

            // sqlite3 /tmp/imdb.db "VACUUM;" -- 121MB => 103 MB
        }

        static void Help()
        {
            Console.WriteLine("Grabber");
            Console.WriteLine("Usage:");
            Console.WriteLine("   Grabber Help");
            Console.WriteLine("   Grabber GenerateImdbDatabase");
            Console.WriteLine("   Grabber UpdateEPG");
            Console.WriteLine("   Grabber Manual <MovieEventId> <ImdbID(tt...)>");
        }

        static void UpdateDatabaseEpg()
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get the connection string
            string connectionString = configuration.GetConnectionString("FxMoviesDb");

            Console.WriteLine("Using database: {0}", connectionString);

            using (var db = FxMoviesDbContextFactory.Create(connectionString))
            {
                DateTime now = DateTime.Now;

                // Remove all old MovieEvents
                {
                    var set = db.MovieEvents;
                    set.RemoveRange(set.Where(x => x.StartTime < now.Date));
                }

                int maxDays;
                if (!int.TryParse(configuration.GetSection("Grabber")["MaxDays"], out maxDays))
                    maxDays = 7;

                for (int days = 0; days <= maxDays; days++)
                {
                    DateTime date = now.Date.AddDays(days);
                    var movies = HumoGrabber.GetGuide(date).Result;

                    Console.WriteLine(date.ToString());
                    foreach (var movie in movies)
                    {
                        Console.WriteLine("{0} {1} {2} {4}", movie.Channel.Name, movie.Title, movie.Year, movie.Channel.Name, movie.StartTime);
                    }

                    var existingMovies = db.MovieEvents.Where(x => x.StartTime.Date == date);
                    Console.WriteLine("Existing movies: {0}", existingMovies.Count());
                    Console.WriteLine("New movies: {0}", movies.Count());

                    // Update channels
                    foreach (var channel in movies.Select(m => m.Channel).Distinct())
                    {
                        Channel existingChannel = db.Channels.Find(channel.Code);
                        if (existingChannel != null)
                        {
                            existingChannel.Name = channel.Name;
                            existingChannel.LogoS = channel.LogoS;
                            existingChannel.LogoM = channel.LogoM;
                            existingChannel.LogoL = channel.LogoL;
                            db.Channels.Update(existingChannel);
                            foreach (var movie in movies.Where(m => m.Channel == channel))
                                movie.Channel = existingChannel;
                        }
                        else
                        {
                            db.Channels.Add(channel);
                        }
                    }

                    // Remove exising movies that don't appear in new movies
                    {
                        var remove = existingMovies.Where(m1 => !movies.Any(m2 => m2.Id == m1.Id));
                        Console.WriteLine("Existing movies to be removed: {0}", remove.Count());
                        db.RemoveRange(remove);
                    }

                    // Update movies
                    foreach (var movie in movies)
                    {
                        var existingMovie = db.MovieEvents.Find(movie.Id);
                        if (existingMovie != null)
                        {
                            existingMovie.Title = movie.Title;
                            existingMovie.Year = movie.Year;
                            existingMovie.StartTime = movie.StartTime;
                            existingMovie.EndTime = movie.EndTime;
                            existingMovie.Channel = movie.Channel;
                            existingMovie.PosterS = movie.PosterS;
                            existingMovie.PosterM = movie.PosterM;
                            existingMovie.PosterL = movie.PosterL;
                            existingMovie.Duration = movie.Duration;
                            existingMovie.Genre = movie.Genre;
                            existingMovie.Content = movie.Content;
                        }
                        else
                        {
                            db.MovieEvents.Add(movie);
                        }
                    }

                    // {
                    //     set.RemoveRange(set.Where(x => x.StartTime.Date == date));
                    //     db.SaveChanges();
                    // }

                    db.SaveChanges();
                }
            }
        }

        static void UpdateEpgDataWithImdb()
        {
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.basics.tsv.gz title.basics.tsv.gz
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.ratings.tsv.gz title.ratings.tsv.gz
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get the connection string
            string connectionStringMovies = configuration.GetConnectionString("FxMoviesDb");
            string connectionStringImdb = configuration.GetConnectionString("ImdbDb");

            using (var dbMovies = FxMoviesDbContextFactory.Create(connectionStringMovies))
            using (var dbImdb = ImdbDbContextFactory.Create(connectionStringImdb))
            {
                foreach (var movieEvent in dbMovies.MovieEvents)
                {
                    Movie movie;
                    if (movieEvent.ImdbId != null)
                    {
                        movie = dbImdb.Movies.Find(movieEvent.ImdbId);
                    }
                    else
                    {
                        movie = dbImdb.Movies.FirstOrDefault(m => 
                            m.PrimaryTitle.Equals(movieEvent.Title, StringComparison.InvariantCultureIgnoreCase) 
                                && (!m.Year.HasValue || m.Year == movieEvent.Year));
                        if (movie == null)
                            movie = dbImdb.Movies.FirstOrDefault(m =>
                                m.OriginalTitle.Equals(movieEvent.Title, StringComparison.InvariantCultureIgnoreCase) 
                                    && (!m.Year.HasValue || m.Year == movieEvent.Year));
                    }
                    
                    if (movie == null)
                    {
                        Console.WriteLine("UpdateEpgDataWithImdb: Could not find movie '{0} ({1})' in IMDB", movieEvent.Title, movieEvent.Year);
                        continue;
                    }

                    Console.WriteLine("{0} ({1}) ==> {2}", movieEvent.Title, movieEvent.Year, movie.Id);
                    movieEvent.ImdbId = movie.Id;
                    movieEvent.ImdbRating = movie.Rating;
                    movieEvent.ImdbVotes = movie.Votes;
                }

                dbMovies.SaveChanges();
            }
        }

        static void ImportImdbData_Movies()
        {
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.basics.tsv.gz title.basics.tsv.gz
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.ratings.tsv.gz title.ratings.tsv.gz

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get the connection string
            string connectionString = configuration.GetConnectionString("ImdbDb");

            using (var db = ImdbDbContextFactory.Create(connectionString))
            {
                var fileToDecompress = new FileInfo(configuration.GetSection("Grabber")["ImdbMoviesList"]);
                using (var originalFileStream = fileToDecompress.OpenRead())
                using (var decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
                using (var textReader = new StreamReader(decompressionStream))
                {
                    int count = 0, skipped = 0;
                    string text;

                    // tconst	titleType	primaryTitle	originalTitle	isAdult	startYear	endYear	runtimeMinutes	genres
                    // tt0000009	movie	Miss Jerry	Miss Jerry	0	1894	\N	45	Romance
                    var regex = new Regex(@"^([^\t]*)\t([^\t]*)\t([^\t]*)\t([^\t]*)\t[^\t]*\t([^\t]*)\t[^\t]*\t[^\t]*\t[^\t]*$",
                        RegexOptions.Compiled);

                    // Skip header
                    textReader.ReadLine();

                    var FilterTypes = new string[]
                    {
                        "movie",
                        "video",
                        "short",
                        "tvMovie",
                        "tvMiniSeries",
                    };

                    while ((text = textReader.ReadLine()) != null)
                    {
                        count++;

                        if (count % 10000 == 0)
                        {
                            Console.WriteLine("UpdateImdbDataWithMovies: {1} records done ({0}%), {2} records skipped ({3}%)", 
                                originalFileStream.Position * 100 / originalFileStream.Length, 
                                count, 
                                skipped, 
                                skipped * 100 / count);
                            db.SaveChanges();
                        }

                        var match = regex.Match(text);
                        if (!match.Success)
                        {
                            Console.WriteLine("Unable to parse line {0}: {1}", count, text);
                            continue;
                        }

                        // Filter on movie|video|short|tvMovie|tvMiniSeries
                        if (!FilterTypes.Contains(match.Groups[2].Value))
                        {
                            skipped++;
                            continue;
                        }

                        string movieId = match.Groups[1].Value;

                        ImdbDB.Movie movie = db.Movies.Find(movieId);
                        if (movie == null)
                        {
                            movie = new ImdbDB.Movie();
                            movie.Id = match.Groups[1].Value;
                            db.Movies.Add(movie);
                        }

                        movie.PrimaryTitle = match.Groups[3].Value;
                        movie.OriginalTitle = match.Groups[4].Value;
                        if (int.TryParse(match.Groups[5].Value, out int startYear))
                            movie.Year = startYear;
                    }

                    Console.WriteLine("IMDB movies scanned: {0}", count);
                }

                db.SaveChanges();
            }
        }        
        static void ImportImdbData_Ratings()
        {
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.basics.tsv.gz title.basics.tsv.gz
            // aws s3api get-object --request-payer requester --bucket imdb-datasets --key documents/v1/current/title.ratings.tsv.gz title.ratings.tsv.gz

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get the connection string
            string connectionString = configuration.GetConnectionString("ImdbDb");

            using (var db = ImdbDbContextFactory.Create(connectionString))
            {
                var fileToDecompress = new FileInfo(configuration.GetSection("Grabber")["ImdbRatingsList"]);
                using (var originalFileStream = fileToDecompress.OpenRead())
                using (var decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
                using (var textReader = new StreamReader(decompressionStream))
                {
                    int count = 0, skipped = 0;
                    string text;

                    // New  Distribution  Votes  Rank  Title
                    //       0000000125  1852213   9.2  The Shawshank Redemption (1994)
                    var regex = new Regex(@"^([^\t]*)\t(\d+\.\d+)\t(\d*)$",
                        RegexOptions.Compiled);

                    // Skip header
                    textReader.ReadLine();

                    while ((text = textReader.ReadLine()) != null)
                    {
                        count++;

                        if (count % 10000 == 0)
                        {
                            Console.WriteLine("UpdateImdbDataWithRatings: {1} records done ({0}%), {2} records skipped", 
                                originalFileStream.Position * 100 / originalFileStream.Length, 
                                count, 
                                skipped,
                                skipped * 100 / count);
                            db.SaveChanges();
                        }

                        var match = regex.Match(text);
                        if (!match.Success)
                        {
                            Console.WriteLine("Unable to parse line {0}: {1}", count, text);
                            continue;
                        }

                        string tconst = match.Groups[1].Value;

                        var movie = db.Movies.Find(tconst);
                        if (movie == null)
                        {
                            // Probably a serie or ...
                            //Console.WriteLine("Unable to find movie {0}", tconst);
                            skipped++;
                            continue;
                        }

                        int votes;
                        if (int.TryParse(match.Groups[3].Value, out votes))
                            movie.Votes = votes;

                        decimal rating;
                        if (decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture.NumberFormat, out rating))
                            movie.Rating = (int)(rating * 10);
                    }

                    Console.WriteLine("IMDB ratings scanned: {0}", count);
                }

                db.SaveChanges();
            }
        }
        
        static void UpdateEpgDataWithImdbManual(int movieEventId, string imdbId)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Get the connection string
            string connectionStringMovies = configuration.GetConnectionString("FxMoviesDb");
            string connectionStringImdb = configuration.GetConnectionString("ImdbDb");

            using (var dbMovies = FxMoviesDbContextFactory.Create(connectionStringMovies))
            using (var dbImdb = ImdbDbContextFactory.Create(connectionStringImdb))
            {
                var movieEvent = dbMovies.MovieEvents.Find(movieEventId);

                if (movieEvent == null)
                {
                    Console.WriteLine("UpdateEpgDataWithImdbManual: Unable to find MovieEvent with ID {0}", movieEventId);
                    return;
                }

                Console.WriteLine("MovieEvent: {0} ({1}), ID {2}, Current ImdbID={3}", 
                    movieEvent.Title, movieEvent.Year, movieEvent.Id, movieEvent.ImdbId);
                    
                var movie = dbImdb.Movies.Find(imdbId);

                if (movie == null)
                {
                    Console.WriteLine("UpdateEpgDataWithImdbManual: Unable to find IMDB movie with ID {0}", imdbId);
                    return;
                }

                Console.WriteLine("IMDB: {0} ({1}), ImdbID={2}", 
                    movie.PrimaryTitle, movie.Year, movie.Id);
                    
                movieEvent.ImdbId = movie.Id;
                movieEvent.ImdbRating = movie.Rating;
                movieEvent.ImdbVotes = movie.Votes;

                dbMovies.SaveChanges();
            }
        }
    }
}
