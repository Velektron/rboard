﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;

using Rboard.Model;

namespace Rboard.Services
{
    public class ReportService
    {
        private static readonly Regex ArchiveRegex = new Regex(@"^Archive_(?<Date>[0-9_-]+).zip$", RegexOptions.Compiled);
        private static readonly Regex ReportArchiveRegex = new Regex(@"^(?<Name>.*)_(?<Date>[0-9_-]+).html$", RegexOptions.Compiled);

        public IEnumerable<Report> Reports
        {
            get
            {
                reloadTask.Wait();
                return reportCategories.SelectMany(p => p.Value);
            }
        }
        public DirectoryInfo ReportsBaseDirectory { get; private set; } = new DirectoryInfo(".");
        public DirectoryInfo ArchiveDirectory { get; private set; } = new DirectoryInfo("Archives");

        internal IConfigurationRoot Configuration { get; }
        internal RService RService { get; }

        private Dictionary<string, List<Report>> reportCategories = new Dictionary<string, List<Report>>();
        private ConcurrentDictionary<Report, Task<string>> reportGenerationTasks = new ConcurrentDictionary<Report, Task<string>>();

        private Task reloadTask;

        public ReportService(IConfigurationRoot configuration, RService rService)
        {
            Configuration = configuration;
            RService = rService;

            Configuration.GetReloadToken().RegisterChangeCallback(s => reloadTask = ReloadConfiguration(), null);
            reloadTask = ReloadConfiguration();
        }

        public Task ReloadReports()
        {
            Configuration.Reload();
            return reloadTask = ReloadConfiguration();
        }

        public Report FindReport(string name)
        {
            reloadTask.Wait();
            return Reports.SingleOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        public Report FindReport(string category, string name)
        {
            reloadTask.Wait();

            List<Report> categoryReports;
            if (!reportCategories.TryGetValue(category.ToLower(), out categoryReports))
                return null;

            return categoryReports.FirstOrDefault(r => string.Equals(r.GetUrl(), name, StringComparison.OrdinalIgnoreCase));
        }

        private Task<Report> LoadReport(string category, IConfigurationSection reportSection)
        {
            return Task.Run(() =>
            {
                string name = reportSection.GetValue<string>("Name");
                string path = reportSection.GetValue<string>("Path");
                string refreshTime = reportSection.GetValue<string>("RefreshTime");
                string archiveTime = reportSection.GetValue<string>("ArchiveTime");
                string deleteTime = reportSection.GetValue<string>("DeleteTime");

                Report report = new Report();

                report.Category = category;
                report.Path = Path.IsPathRooted(path) ? path : Path.Combine(ReportsBaseDirectory.FullName, path);
                report.Name = name ?? Path.GetFileNameWithoutExtension(report.Path);

                if (refreshTime != null) report.RefreshTime = Utils.ParseTime(refreshTime);
                if (archiveTime != null) report.ArchiveTime = Utils.ParseTime(archiveTime);
                if (deleteTime != null) report.DeleteTime = Utils.ParseTime(deleteTime);

                using (StreamReader reader = new StreamReader(report.Path))
                {
                    string line = reader.ReadLine();

                    if (line == "---")
                    {
                        while (!reader.EndOfStream)
                        {
                            line = reader.ReadLine();
                            if (line == "---")
                                break;

                            int separator = line.IndexOf(':');
                            if (separator < 0)
                                break;

                            string key = line.Remove(separator).Trim();
                            string value = line.Substring(separator + 1).Trim();

                            switch (key)
                            {
                                case "orientation":
                                    report.Configuration[key] = value;
                                    break;
                            }
                        }
                    }
                }

                return report;
            });
        }
        public string GetLastGeneratedReport(Report report)
        {
            reloadTask.Wait();

            FileInfo reportInfo = new FileInfo(report.Path);
            FileInfo generatedReportInfo = new FileInfo(Path.Combine(reportInfo.Directory.FullName, report.GetGeneratedReportName()));

            if (generatedReportInfo.Exists)
                return generatedReportInfo.FullName;
            else
                return null;
        }
        public Task<string> UpdateReport(Report report, bool force = false)
        {
            reloadTask.Wait();

            FileInfo reportInfo = new FileInfo(report.Path);
            FileInfo generatedReportInfo = new FileInfo(Path.Combine(reportInfo.Directory.FullName, report.GetGeneratedReportName()));

            DateTime now = DateTime.Now;

            // Check if archiving is needed
            if (generatedReportInfo.Exists)
            {
                DateTime oldArchiveDate = Utils.RoundDateTime(generatedReportInfo.LastWriteTime, report.ArchiveTime);
                DateTime newArchiveDate = Utils.RoundDateTime(now, report.ArchiveTime);

                if (newArchiveDate != oldArchiveDate)
                    ArchiveReport(report).Wait();
            }

            // Check if generation is needed
            if (force)
            {
                Task<string> generationTask;
                if (reportGenerationTasks.TryRemove(report, out generationTask))
                    generationTask.Wait();
            }

            if (!force && generatedReportInfo.Exists && now < generatedReportInfo.LastWriteTime + report.RefreshTime)
                return Task.FromResult(generatedReportInfo.FullName);
            else
                return reportGenerationTasks.GetOrAdd(report, GenerateReport);
        }
        private Task<string> GenerateReport(Report report)
        {
            reloadTask.Wait();

            Task<string> generationTask = Task.Run(() =>
            {
                FileInfo reportInfo = new FileInfo(report.Path);
                FileInfo cleanReportInfo = new FileInfo(Path.Combine(reportInfo.Directory.FullName, Path.ChangeExtension(reportInfo.Name.Replace(" ", "_"), ".g.Rmd")));
                FileInfo generatedReportInfo = new FileInfo(Path.Combine(reportInfo.Directory.FullName, report.GetGeneratedReportName()));

                RService.WaitUntilReady();

                Console.WriteLine("[{0}] Generating report {1}", DateTime.Now.ToShortTimeString(), reportInfo.Name);

                // Clean info in report
                using (StreamReader reader = reportInfo.OpenText())
                {
                    using (FileStream writerStream = cleanReportInfo.Exists ? cleanReportInfo.OpenWrite() : cleanReportInfo.Create())
                    using (StreamWriter writer = new StreamWriter(writerStream))
                    {
                        string line = reader.ReadLine();

                        // Skip header
                        if (line == "---")
                            while (reader.ReadLine() != "---") ;

                        // Generate custom header
                        writer.WriteLine("---");
                        writer.WriteLine("title: \"{0}\"", report.Name);
                        writer.WriteLine("output:");
                        writer.WriteLine("  flexdashboard::flex_dashboard:");
                        writer.WriteLine("    self_contained: FALSE");
                        writer.WriteLine("    lib_dir: \"libraries\"");
                        writer.WriteLine("    css: \"/libraries/flex-custom.css\"");

                        if (report.Configuration.ContainsKey("orientation"))
                            writer.WriteLine("    orientation: {0}", report.Configuration["orientation"]);

                        writer.WriteLine("---");

                        if (line != "---")
                            writer.WriteLine(line);

                        // Copy the file
                        while (!reader.EndOfStream)
                            writer.WriteLine(reader.ReadLine());
                    }
                }

                // Render the report
                try
                {
                    Task renderTask = RService.Render(cleanReportInfo.FullName, generatedReportInfo.FullName);
                    renderTask.Wait();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[{0}] Error while generating report {1}: {2}", DateTime.Now.ToShortTimeString(), reportInfo.Name, e);
                    throw;
                }

                // Clean everything
                cleanReportInfo.Delete();

                Console.WriteLine("[{0}] Generated report {1}", DateTime.Now.ToShortTimeString(), reportInfo.Name);

                return generatedReportInfo.FullName;
            });

            generationTask.ContinueWith(t => reportGenerationTasks.TryRemove(report, out t));
            return generationTask;
        }

        private async Task ArchiveReport(Report report)
        {
            reloadTask.Wait();

            await Task.Run(() =>
            {
                FileInfo reportInfo = new FileInfo(report.Path);
                FileInfo generatedReportInfo = new FileInfo(Path.Combine(reportInfo.Directory.FullName, report.GetGeneratedReportName()));

                DateTime now = DateTime.Now;

                // Check if archiving is needed
                if (generatedReportInfo.Exists)
                {
                    DateTime oldArchiveDate = Utils.RoundDateTime(generatedReportInfo.LastWriteTime, report.ArchiveTime);
                    DateTime newArchiveDate = Utils.RoundDateTime(now, report.ArchiveTime);

                    if (newArchiveDate != oldArchiveDate)
                    {
                        string archiveName = GetArchiveName(oldArchiveDate);
                        FileInfo archiveInfo = new FileInfo(Path.Combine(ArchiveDirectory.FullName, archiveName));

                        if (!ArchiveDirectory.Exists)
                            ArchiveDirectory.Create();

                        if (!archiveInfo.Exists)
                        {
                            using (FileStream archiveStream = archiveInfo.Create())
                            using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
                                archive.ToString();
                        }

                        using (FileStream archiveStream = archiveInfo.Open(FileMode.Open))
                        using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Update))
                        {
                            string reportArchiveName = report.GetArchivedReportName(oldArchiveDate);

                            ZipArchiveEntry reportArchiveEntry = archive.GetEntry(reportArchiveName);
                            if (reportArchiveEntry == null)
                            {
                                reportArchiveEntry = archive.CreateEntry(reportArchiveName, CompressionLevel.Optimal);

                                using (FileStream reportStream = generatedReportInfo.OpenRead())
                                using (Stream reportArchiveStream = reportArchiveEntry.Open())
                                    reportStream.CopyTo(reportArchiveStream);
                            }
                        }
                    }
                }
            });

            await CleanArchives(report);
        }
        public Task CleanArchives(Report report)
        {
            return Task.Run(() =>
            {
                DateTime deleteTime = DateTime.Now - report.DeleteTime;

                FileInfo[] archivesInfo = ArchiveDirectory.GetFiles();
                foreach (FileInfo archiveInfo in archivesInfo)
                {
                    Match archiveName = ArchiveRegex.Match(archiveInfo.Name);
                    if (!archiveName.Success)
                        continue;

                    DateTime archiveDate;
                    if (!DateTime.TryParse(archiveName.Groups["Date"].Value, out archiveDate))
                        continue;

                    if (archiveDate > deleteTime.Date)
                        continue;

                    bool deleteArchive = false;

                    using (FileStream archiveStream = archiveInfo.Open(FileMode.Open))
                    using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Update))
                    {
                        foreach (ZipArchiveEntry archiveEntry in archive.Entries.ToArray())
                        {
                            Match reportArchiveName = ReportArchiveRegex.Match(archiveEntry.Name);
                            if (!reportArchiveName.Success)
                                continue;

                            DateTime reportArchiveDate;
                            if (!DateTime.TryParse(reportArchiveName.Groups["Date"].Value, out reportArchiveDate))
                                continue;

                            if (reportArchiveDate > deleteTime)
                                continue;

                            if (archiveEntry.Name != report.GetArchivedReportName(reportArchiveDate))
                                continue;

                            archiveEntry.Delete();
                        }

                        deleteArchive = archive.Entries.Count == 0;
                    }

                    if (deleteArchive)
                        archiveInfo.Delete();
                }
            });
        }
        public IEnumerable<DateTime> EnumerateReportArchives(Report report, bool fileCheck = false)
        {
            DateTime archiveDate = Utils.RoundDateTime(DateTime.Now, report.ArchiveTime) - report.ArchiveTime;
            DateTime deletionDate = DateTime.Now - report.DeleteTime;

            for (; archiveDate > deletionDate; archiveDate -= report.ArchiveTime)
            {
                if (fileCheck)
                {
                    if (!ArchiveDirectory.Exists)
                        continue;

                    string archiveName = GetArchiveName(archiveDate);
                    FileInfo archiveInfo = new FileInfo(Path.Combine(ArchiveDirectory.FullName, archiveName));

                    if (!archiveInfo.Exists)
                        continue;

                    using (FileStream archiveStream = archiveInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
                    {
                        string reportArchiveName = report.GetArchivedReportName(archiveDate);

                        ZipArchiveEntry reportArchiveEntry = archive.GetEntry(reportArchiveName);
                        if (reportArchiveEntry == null)
                            continue;
                    }

                    yield return archiveDate;
                }
                else
                    yield return archiveDate;
            }
        }
        public Task<string> GetReportArchive(Report report, DateTime date)
        {
            return Task.Run(() =>
            {
                if (!ArchiveDirectory.Exists)
                    return null;

                string archiveName = GetArchiveName(date);
                FileInfo archiveInfo = new FileInfo(Path.Combine(ArchiveDirectory.FullName, archiveName));

                if (!archiveInfo.Exists)
                    return null;

                using (FileStream archiveStream = archiveInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
                {
                    string reportArchiveName = report.GetArchivedReportName(date);

                    ZipArchiveEntry reportArchiveEntry = archive.GetEntry(reportArchiveName);
                    if (reportArchiveEntry == null)
                        return null;

                    using (StreamReader reader = new StreamReader(reportArchiveEntry.Open()))
                        return reader.ReadToEnd();
                }
            });
        }
        private static string GetArchiveName(DateTime date)
        {
            return string.Format("Archive_{0:yyyy-MM-dd}.zip", date);
        }

        private Task ReloadConfiguration()
        {
            return Task.Run(() =>
            {
                IConfigurationSection rboardSection = Configuration.GetSection("Rboard");
                if (rboardSection != null)
                {
                    IConfigurationSection reportsDirectorySection = rboardSection.GetSection("ReportsDirectory");
                    if (reportsDirectorySection?.Value != null)
                        ReportsBaseDirectory = new DirectoryInfo(reportsDirectorySection.Value);

                    IConfigurationSection archivesDirectory = rboardSection.GetSection("ArchivesDirectory");
                    if (archivesDirectory?.Value != null)
                        ArchiveDirectory = new DirectoryInfo(archivesDirectory.Value);
                }

                IConfigurationSection reportsSection = Configuration.GetSection("Reports");
                if (reportsSection != null)
                {
                    reportCategories.Clear();
                    foreach (IConfigurationSection categorySection in reportsSection.GetChildren())
                    {
                        string category = categorySection.Key;
                        string categoryKey = category.ToLower();

                        List<Report> categoryReports;
                        if (!reportCategories.TryGetValue(categoryKey, out categoryReports))
                            reportCategories.Add(categoryKey, categoryReports = new List<Report>());

                        foreach (IConfigurationSection child in categorySection.GetChildren())
                        {
                            Task<Report> reportLoadingTask = LoadReport(category, child);
                            reportLoadingTask.Wait();

                            categoryReports.Add(reportLoadingTask.Result);
                        }
                    }
                }
            });
        }
    }

    public static class ReportExtensions
    {
        public static string GetUrl(this Report report)
        {
            return report.Name.Replace(" ", "-").Replace("--", "-").Replace("--", "-").ToLower();
        }

        public static string GetGeneratedReportName(this Report report)
        {
            return Path.ChangeExtension(Path.GetFileName(report.Path).Replace(" ", "_"), "html");
        }
        public static string GetArchivedReportName(this Report report, DateTime date)
        {
            return string.Format("{0}_{1}.html", Path.GetFileNameWithoutExtension(report.Path).Replace(" ", "_"), Utils.FormatDateTime(date, "yyyy-MM-dd", "yyyy-MM-dd_HH-mm"));
        }
    }
}