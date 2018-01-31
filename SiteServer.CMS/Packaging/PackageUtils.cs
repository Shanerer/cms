﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NuGet;
using NuGet.Packaging;
using SiteServer.CMS.Plugin;
using SiteServer.Utils;

namespace SiteServer.CMS.Packaging
{
    // https://blog.nuget.org/20130520/Play-with-packages.html
    // https://haacked.com/archive/2011/01/15/building-a-self-updating-site-using-nuget.aspx/
    // https://github.com/caleb-vear/NuSelfUpdate
    public class PackageUtils
    {
        public const string PackageIdSsCms = "SS.CMS";
        public const string VersionDev = "0.0.0";

        private const string NuGetPackageSource = "https://packages.nuget.org/api/v2";
        private const string MyGetPackageSource = "https://www.myget.org/F/siteserver/api/v2";

        public static bool FindLastPackage(string packageId, out string title, out string version, out DateTimeOffset? published, out string releaseNotes)
        {
            title = string.Empty;
            version = string.Empty;
            published = null;
            releaseNotes = string.Empty;

            try
            {
                var repo =
                    PackageRepositoryFactory.Default.CreateRepository(WebConfigUtils.AllowNightlyBuild
                        ? MyGetPackageSource
                        : NuGetPackageSource);

                var package = repo.FindPackage(packageId);

                title = package.Title;
                version = package.Version.ToString();
                published = package.Published;
                releaseNotes = package.ReleaseNotes;

                return true;
            }
            catch (Exception)
            {
                // ignored
            }

            return false;
        }

        public static void DownloadPackage(string packageId, string version)
        {
            var packagesPath = PathUtils.GetPackagesPath();
            var idWithVersion = $"{packageId}.{version}";
            var directoryPath = PathUtils.Combine(packagesPath, idWithVersion);

            if (DirectoryUtils.IsDirectoryExists(directoryPath))
            {
                if (FileUtils.IsFileExists(PathUtils.Combine(directoryPath, $"{idWithVersion}.nupkg")) && FileUtils.IsFileExists(PathUtils.Combine(directoryPath, $"{packageId}.nuspec")))
                {
                    return;
                }
            }

            var repo = PackageRepositoryFactory.Default.CreateRepository(WebConfigUtils.AllowNightlyBuild
                ? MyGetPackageSource
                : NuGetPackageSource);

            var packageManager = new PackageManager(repo, packagesPath);

            //Download and unzip the package
            packageManager.InstallPackage(packageId, SemanticVersion.Parse(version), true, WebConfigUtils.AllowPrereleaseVersions);

            ZipUtils.UnpackFilesByExtension(PathUtils.Combine(directoryPath, idWithVersion + ".nupkg"),
                directoryPath, ".nuspec");
        }

        public static bool UpdatePackage(string idWithVersion, bool isSsCms, out string errorMessage)
        {
            try
            {
                var packagePath = PathUtils.GetPackagesPath(idWithVersion);

                string nuspecPath;
                string dllDirectoryPath;
                var metadata = GetPackageMetadataFromPackages(idWithVersion, out nuspecPath, out dllDirectoryPath, out errorMessage);
                if (metadata == null)
                {
                    return false;
                }

                if (isSsCms)
                {
                    var packageWebConfigPath = PathUtils.Combine(packagePath, WebConfigUtils.WebConfigFileName);
                    if (!FileUtils.IsFileExists(packageWebConfigPath))
                    {
                        errorMessage = $"升级包 {WebConfigUtils.WebConfigFileName} 文件不存在";
                        return false;
                    }

                    WebConfigUtils.UpdateWebConfig(packageWebConfigPath, WebConfigUtils.IsProtectData,
                        WebConfigUtils.DatabaseType, WebConfigUtils.ConnectionString, WebConfigUtils.AdminDirectory,
                        WebConfigUtils.SecretKey);

                    DirectoryUtils.Copy(PathUtils.Combine(packagePath, DirectoryUtils.SiteFiles.DirectoryName),
                        PathUtils.GetSiteFilesPath(string.Empty), true);
                    DirectoryUtils.Copy(PathUtils.Combine(packagePath, DirectoryUtils.SiteServer.DirectoryName),
                        PathUtils.GetAdminDirectoryPath(string.Empty), true);
                    DirectoryUtils.Copy(PathUtils.Combine(packagePath, DirectoryUtils.Bin.DirectoryName),
                        PathUtils.GetBinDirectoryPath(string.Empty), true);
                    FileUtils.CopyFile(packageWebConfigPath,
                        PathUtils.Combine(WebConfigUtils.PhysicalApplicationPath, WebConfigUtils.WebConfigFileName),
                        true);
                }
                else
                {
                    var pluginPath = PathUtils.GetPluginPath(metadata.Id);
                    DirectoryUtils.CreateDirectoryIfNotExists(pluginPath);

                    DirectoryUtils.Copy(PathUtils.Combine(packagePath, "content"), pluginPath, true);
                    DirectoryUtils.Copy(dllDirectoryPath, PathUtils.Combine(pluginPath, "Bin"), true);

                    var configFilelPath = PathUtils.Combine(pluginPath, $"{metadata.Id}.nuspec");
                    FileUtils.CopyFile(nuspecPath, configFilelPath, true);

                    PluginManager.ClearCache();
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }

            return true;
        }

        public static PackageMetadata GetPackageMetadataFromPlugins(string directoryName, out string dllDirectoryPath, out string errorMessage)
        {
            dllDirectoryPath = string.Empty;
            var nuspecPath = PathUtils.GetPluginNuspecPath(directoryName);
            if (!File.Exists(nuspecPath))
            {
                errorMessage = $"插件配置文件 {directoryName}.nuspec 不存在";
                return null;
            }
            dllDirectoryPath = PathUtils.GetPluginDllDirectoryPath(directoryName);
            if (string.IsNullOrEmpty(dllDirectoryPath))
            {
                errorMessage = $"插件可执行文件 {directoryName}.dll 不存在";
                return null;
            }

            PackageMetadata metadata;
            try
            {
                metadata = GetPackageMetadata(nuspecPath);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }

            if (string.IsNullOrEmpty(metadata.Id))
            {
                errorMessage = "插件配置文件不正确";
                return null;
            }

            errorMessage = string.Empty;
            return metadata;
        }

        public static PackageMetadata GetPackageMetadataFromPackages(string directoryName, out string nuspecPath, out string dllDirectoryPath, out string errorMessage)
        {
            nuspecPath = string.Empty;
            dllDirectoryPath = string.Empty;
            errorMessage = string.Empty;

            var directoryPath = PathUtils.GetPackagesPath(directoryName);

            foreach (var filePath in DirectoryUtils.GetFilePaths(directoryPath))
            {
                if (StringUtils.EqualsIgnoreCase(Path.GetExtension(filePath), ".nuspec"))
                {
                    nuspecPath = filePath;
                    break;
                }
            }

            if (string.IsNullOrEmpty(nuspecPath))
            {
                errorMessage = "插件配置文件不存在";
                return null;
            }

            PackageMetadata metadata;
            try
            {
                metadata = GetPackageMetadata(nuspecPath);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }

            var packageId = metadata.Id;

            if (string.IsNullOrEmpty(packageId))
            {
                errorMessage = $"插件配置文件 {nuspecPath} 不正确";
                return null;
            }

            if (!StringUtils.EqualsIgnoreCase(packageId, PackageIdSsCms))
            {
                //https://docs.microsoft.com/en-us/nuget/schema/target-frameworks#supported-frameworks

                foreach (var dirName in DirectoryUtils.GetDirectoryNames(PathUtils.Combine(directoryPath, "lib")))
                {
                    if (StringUtils.StartsWithIgnoreCase(dirName, "net45") ||
                        StringUtils.StartsWithIgnoreCase(dirName, "net451") ||
                        StringUtils.StartsWithIgnoreCase(dirName, "net452") ||
                        StringUtils.StartsWithIgnoreCase(dirName, "net46") ||
                        StringUtils.StartsWithIgnoreCase(dirName, "net461") ||
                        StringUtils.StartsWithIgnoreCase(dirName, "net462"))
                    {
                        dllDirectoryPath = PathUtils.Combine(directoryPath, "lib", dirName);
                        break;
                    }
                }
                if (string.IsNullOrEmpty(dllDirectoryPath))
                {
                    dllDirectoryPath = PathUtils.Combine(directoryPath, "lib");
                }

                if (!FileUtils.IsFileExists(PathUtils.Combine(dllDirectoryPath, packageId + ".dll")))
                {
                    errorMessage = $"插件可执行文件 {packageId}.dll 不存在";
                    return null;
                }
            }
            
            return metadata;
        }

        private static PackageMetadata GetPackageMetadata(string nuspecPath)
        {
            var nuspecReader = new NuspecReader(nuspecPath);

            var rawMetadata = nuspecReader.GetMetadata();
            if (rawMetadata == null || !rawMetadata.Any()) return null;

            var metadata = PackageMetadata.FromNuspecReader(nuspecReader);
            return metadata;
        }

        //**********************************test********************************

        public static string TestGetLastPackage(bool isPreviewVersion)
        {
            var packageID = "Newtonsoft.Json";

            //Connect to the official package repository
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository("https://packages.nuget.org/api/v2");

            IPackage p = repo.FindPackage(packageID);

            var builder = new StringBuilder();
            builder.Append(p.GetFullName()).Append("<br />");

            return builder.ToString();
        }

        public static string TestGetReleaseVersionList()
        {
            var packageID = "Newtonsoft.Json";

            //Connect to the official package repository
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository("https://packages.nuget.org/api/v2");

            //Get the list of all NuGet packages with ID 'EntityFramework'       
            List<IPackage> packages = repo.FindPackagesById(packageID).ToList();

            //Filter the list of packages that are not Release (Stable) versions
            packages = packages.Where(item => (item.IsReleaseVersion())).ToList();

            packages.Reverse();

            //Iterate through the list and print the full name of the pre-release packages to console
            var builder = new StringBuilder();
            foreach (IPackage p in packages)
            {
                builder.Append(p.GetFullName()).Append("<br />");
            }

            return builder.ToString();
        }

        public static void TestGetAndInstall()
        {
            //ID of the package to be looked up
            string packageID = "EntityFramework";

            //Connect to the official package repository
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository("https://packages.nuget.org/api/v2");

            //Initialize the package manager
            string path = PathUtils.GetPackagesPath();
            PackageManager packageManager = new PackageManager(repo, path);

            //Download and unzip the package
            packageManager.InstallPackage(packageID, SemanticVersion.Parse("5.0.0"));
        }

        public static string TestGet10()
        {
            string url = "https://www.nuget.org/api/v2/";
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository(url);
            var packages = repo
                .GetPackages()
                .Where(p => p.IsLatestVersion)
                .OrderByDescending(p => p.DownloadCount)
                .Take(10);

            var builder = new StringBuilder();

            foreach (IPackage package in packages)
            {
                builder.Append(package);
            }

            return builder.ToString();
        }

        //public static async Task<string> GetMetadata()
        //{
        //    Logger logger = new Logger();
        //    List<Lazy<INuGetResourceProvider>> providers = new List<Lazy<INuGetResourceProvider>>();
        //    providers.AddRange(Repository.Provider.GetCoreV3());  // Add v3 API support
        //    PackageSource packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
        //    SourceRepository sourceRepository = new SourceRepository(packageSource, providers);

        //    IPackageMetadata

        //    PackageMetadataResource packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();
        //    IEnumerable<IPackageSearchMetadata> searchMetadata = await packageMetadataResource.GetMetadataAsync("Wyam.Core", true, true, logger, CancellationToken.None);
        //    var builder = new StringBuilder();
        //    foreach (var packageSearchMetadata in searchMetadata)
        //    {
        //        builder.Append(packageSearchMetadata.);
        //        builder.Append("//////////////////////////////////////////////////////////");
        //    }
        //    return builder.ToString();
        //}
    }

    //public class Logger : ILogger
    //{
    //    public void LogDebug(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogDebug));
    //    }

    //    public void LogVerbose(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogVerbose));
    //    }

    //    public void LogInformation(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogInformation));
    //    }

    //    public void LogMinimal(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogMinimal));
    //    }

    //    public void LogWarning(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogWarning));
    //    }

    //    public void LogError(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogError));
    //    }

    //    public void LogInformationSummary(string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(LogInformationSummary));
    //    }

    //    public void Log(LogLevel level, string data)
    //    {
    //        DataProvider.RecordDao.RecordLog(data, nameof(Log));
    //    }

    //    public Task LogAsync(LogLevel level, string data)
    //    {
    //        return null;
    //    }

    //    public void Log(ILogMessage message)
    //    {
    //        DataProvider.RecordDao.RecordLog(message.Message, nameof(Log));
    //    }

    //    public Task LogAsync(ILogMessage message)
    //    {
    //        return null;
    //    }
    //}
}
