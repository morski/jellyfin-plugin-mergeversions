using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<MergeVersionsManager> _logger;
        private readonly IFileSystem _fileSystem;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        public async Task MergeMoviesAsync(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(x => x.Count() > 1)
                .ToList();

            var current = 0;
            foreach(var duplicateMovie in duplicateMovies )
            {
                current++;
                var percent = current / (double)duplicateMovies.Count * 100;
                progress?.Report((int)percent);
                _logger.LogInformation(
                    "Merging {name} ({year})", duplicateMovie.ElementAt(0).Name, duplicateMovie.ElementAt(0).ProductionYear
                );
                try
                {
                    await MergeVersions(duplicateMovie.Select(e => e.Id).ToList());
                }
                catch(Exception ex)
                {
                    _logger.LogError("Error when merging movies - {errorMessage}", ex.Message);
                }
            }
            
            progress?.Report(100);
        }

        public async Task SplitMoviesAsync(IProgress<double> progress)
        {
            var movies = GetMoviesFromLibrary();
            var current = 0;
            foreach(var movie in movies)
            {
                current++;
                var percent = current / (double)movies.Count * 100;
                progress?.Report((int)percent);

                _logger.LogInformation("Spliting {name} ({year})", movie.Name, movie.ProductionYear);
                await DeleteAlternateSources(movie.Id);
            }
            
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated episodes");

            var duplicateEpisodes = GetEpisodesFromLibrary()
                .GroupBy(x => new
                {
                    x.SeriesName,
                    x.SeasonName,
                    x.Name,
                    x.IndexNumber,
                    x.ProductionYear
                })
                .Where(x => x.Count() > 1)
                .ToList();

            var current = 0;
            foreach (var e in duplicateEpisodes)
            {
                current++;
                var percent = current / (double)duplicateEpisodes.Count * 100;
                progress?.Report((int)percent);
                _logger.LogInformation(
                    "Merging {name} ({year})", e.ElementAt(0).Name, e.ElementAt(0).ProductionYear
                );
                try
                {
                    await MergeVersions(e.Select(e => e.Id).ToList());
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error when merging shows - {errorMessage}", ex.Message);
                }
            }
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress)
        {
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var e in episodes)
            {
                current++;
                var percent = current / (double)episodes.Count * 100;
                progress?.Report((int)percent);

                _logger.LogInformation("Spliting {indexNumber} ({name})", e.IndexNumber, e.SeriesName);
                await DeleteAlternateSources(e.Id);
            }
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Movie],
                        IsVirtualItem = false,
                        Recursive = true,
                        HasTmdbId = true,
                    }
                )
                .Select(m => m as Movie)
                .Where(IsElegible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsElegible)
                .ToList();
        }

        private async Task MergeVersions(List<Guid> ids)
        {
            var items = ids.Select(i => _libraryManager.GetItemById<BaseItem>(i, null))
                .OfType<Video>()
                .OrderBy(i => i.Id)
                .ToList();

            if (items.Count < 2)
            {
                return;
            }

            var primaryVersion = items.FirstOrDefault(i =>
                i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId)
            );
            if (primaryVersion is null)
            {
                primaryVersion = items
                    .OrderBy(i =>
                    {
                        if (i.Video3DFormat.HasValue || i.VideoType != VideoType.VideoFile)
                        {
                            return 1;
                        }

                        return 0;
                    })
                    .ThenByDescending(i => i.GetDefaultVideoStream()?.Width ?? 0)
                    .First();
            }

            var alternateVersionsOfPrimary = primaryVersion
                .LinkedAlternateVersions.Where(l => items.Any(i => i.Path == l.Path))
                .ToList();

            foreach (var item in items.Where(i => !i.Id.Equals(primaryVersion.Id)))
            {
                item.SetPrimaryVersionId(
                    primaryVersion.Id.ToString("N", CultureInfo.InvariantCulture)
                );

                await item.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);

                if (
                    !alternateVersionsOfPrimary.Any(i =>
                        string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    alternateVersionsOfPrimary.Add(
                        new LinkedChild { Path = item.Path, ItemId = item.Id }
                    );
                }

                foreach (var linkedItem in item.LinkedAlternateVersions)
                {
                    if (
                        !alternateVersionsOfPrimary.Any(i =>
                            string.Equals(
                                i.Path,
                                linkedItem.Path,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        alternateVersionsOfPrimary.Add(linkedItem);
                    }
                }

                if (item.LinkedAlternateVersions.Length > 0)
                {
                    item.LinkedAlternateVersions = [];
                    await item.UpdateToRepositoryAsync(
                            ItemUpdateType.MetadataEdit,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
            }

            primaryVersion.LinkedAlternateVersions = alternateVersionsOfPrimary.ToArray();
            await primaryVersion
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private async Task DeleteAlternateSources(Guid itemId)
        {
            var item = _libraryManager.GetItemById<Video>(itemId);
            if (item is null)
            {
                return;
            }

            if (item.LinkedAlternateVersions.Length == 0 && item.PrimaryVersionId != null)
            {
                item = _libraryManager.GetItemById<Video>(Guid.Parse(item.PrimaryVersionId));
            }

            if (item is null)
            {
                return;
            }

            foreach (var link in item.GetLinkedAlternateVersions())
            {
                link.SetPrimaryVersionId(null);
                link.LinkedAlternateVersions = [];

                await link.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }

            item.LinkedAlternateVersions = [];
            item.SetPrimaryVersionId(null);
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool IsElegible(BaseItem item)
        {
            if (
                Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                && Plugin.Instance.PluginConfiguration.LocationsExcluded.Any(s =>
                    _fileSystem.ContainsSubPath(s, item.Path)
                )
            )
            {
                return false;
            }
            return true;
        }
    }
}
