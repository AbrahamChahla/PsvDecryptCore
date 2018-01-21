﻿using System;
using System.Collections.Async;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PsvDecryptCore.Common;
using PsvDecryptCore.Models;

namespace PsvDecryptCore.Services
{
    internal class DecryptionEngine
    {
        private readonly LoggingService _loggingService;
        private readonly PsvInformation _psvInformation;
        private readonly StringProcessor _stringProcessor;
        private readonly Config _config;

        public DecryptionEngine(PsvInformation psvInformation, LoggingService loggingService,
            StringProcessor stringProcessor, Config config)
        {
            _stringProcessor = stringProcessor;
            _config = config;
            _psvInformation = psvInformation;
            _loggingService = loggingService;
        }

        public async Task StartAsync(int? maxDegreeOfParallelism = null)
        {
            int threads = maxDegreeOfParallelism ?? Environment.ProcessorCount;
            using (var db = new PsvContext(_psvInformation))
            {
                IEnumerable<Course> courses = db.Courses;
                foreach (var course in courses)
                {
                    _loggingService.Log(LogLevel.Information, $"Processing course \"{course.Name}\"...");
                    // Checks
                    string courseSource = Path.Combine(_psvInformation.CoursesPath, course.Name);
                    string courseOutput = Path.Combine(_psvInformation.Output,
                        _stringProcessor.SanitizeTitle(course.Title));
                    if (!Directory.Exists(courseSource))
                    {
                        _loggingService.Log(LogLevel.Warning,
                            $"Courses directory for \"{course.Name}\" not found. Skipping...");
                        continue;
                    }

                    if (!Directory.Exists(courseOutput)) Directory.CreateDirectory(courseOutput);

                    // Course image copy
                    await CopyCourseImageAsync(courseSource, courseOutput).ConfigureAwait(false);

                    // Write course info
                    await WriteCourseInfoAsync(course, courseOutput).ConfigureAwait(false);

                    var modules = await db.Modules.Where(x => x.CourseName == course.Name).ToListAsync()
                        .ConfigureAwait(false);
                    _loggingService.Log(LogLevel.Information,
                        $"Found {modules.Count} modules under course \"{course.Name}\"...");

                    foreach (var module in modules)
                    {
                        // Preps
                        _loggingService.Log(LogLevel.Information, $"Processing module: {module.Name}...");
                        string moduleHash = await Task.Run(() => GetModuleHash(module.Name, module.AuthorHandle))
                            .ConfigureAwait(false);
                        string moduleOutput = Path.Combine(courseOutput,
                            $"{_stringProcessor.TitleToFileIndex(module.ModuleIndex)}. {_stringProcessor.SanitizeTitle(module.Title)}");
                        string moduleSource = Path.Combine(courseSource, moduleHash);
                        if (!Directory.Exists(moduleOutput)) Directory.CreateDirectory(moduleOutput);

                        // Write module info
                        await WriteModuleInfoAsync(module, moduleOutput).ConfigureAwait(false);

                        // Process each clip
                        var clips = await db.Clips.Where(x => x.ModuleId == module.Id).ToListAsync().ConfigureAwait(false);
                        
                        // Bail if no courses are found in database
                        if (clips.Count == 0)
                        {
                            _loggingService.Log(LogLevel.Warning,
                                $"No corresponding clips found for module {module.Name}, skipping...");
                            continue;
                        }

                        // Write clip info
                        await WriteClipInfoAsync(clips, moduleOutput).ConfigureAwait(false);

                        await clips.ParallelForEachAsync(async clip =>
                        {
                            string clipSource = Path.Combine(moduleSource, $"{clip.Name}.psv");
                            string clipName =
                                $"{_stringProcessor.TitleToFileIndex(clip.ClipIndex)}. {_stringProcessor.SanitizeTitle(clip.Title)}";
                            string clipFilePath = Path.Combine(moduleOutput, $"{clipName}.mp4");

                            // Decrypt individual clip
                            await DecryptFileAsync(clipSource, clipFilePath).ConfigureAwait(false);

                            // Create subtitles for each clip
                            if (course.HasTranscript != true) return;
                            using (var psvContext = new PsvContext(_psvInformation))
                            {
                                var transcripts = psvContext.ClipTranscripts.Where(x => x.ClipId == clip.Id);
                                if (!await transcripts.AnyAsync().ConfigureAwait(false)) return;
                                await BuildSubtitlesAsync(transcripts, moduleOutput, clipName).ConfigureAwait(false);
                            }
                        }, threads).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        ///     Builds the <see cref="ClipTranscript" /> to SRT file.
        /// </summary>
        private Task BuildSubtitlesAsync(IEnumerable<ClipTranscript> transcripts, string srtOutput,
            string srtName)
        {
            if (_config.Subtitle.HasSuffix) srtName = srtName + _config.Subtitle.Suffix;
            var clipTranscripts = transcripts as IList<ClipTranscript> ?? transcripts.ToList();
            if (!clipTranscripts.Any()) return Task.CompletedTask;
            var transcriptBuilder = new StringBuilder();
            string transcriptFileOutput = Path.Combine(srtOutput, $"{srtName}.srt");
            int lineCount = 0;
            foreach (var transcript in clipTranscripts)
            {
                lineCount++;
                transcriptBuilder.AppendLine(lineCount.ToString());
                string startTime = TimeSpan.FromMilliseconds(transcript.StartTime).ToString(@"hh\:mm\:ss");
                string endTime = TimeSpan.FromMilliseconds(transcript.EndTime).ToString(@"hh\:mm\:ss");
                transcriptBuilder.AppendLine($"{startTime},{transcript.StartTime % 1000}" +
                                             " --> " +
                                             $"{endTime},{transcript.EndTime % 1000}");
                transcriptBuilder.AppendLine(string.Join("\n",
                    transcript.Text.Replace("\r", "").Split('\n').Select(x => "- " + x)));
                transcriptBuilder.AppendLine();
            }
            _loggingService.Log(LogLevel.Debug, $"Saving {srtName}...");
            return File.WriteAllTextAsync(transcriptFileOutput, transcriptBuilder.ToString());
        }

        /// <summary>
        ///     Gets the required module hash for course directory name.
        /// </summary>
        private static string GetModuleHash(string name, string authorHandle)
        {
            using (var md5 = MD5.Create())
            {
                return Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(name + "|" + authorHandle)))
                    .Replace('/', '_');
            }
        }

        /// <summary>
        ///     Decrypts the selected file.
        /// </summary>
        private async Task DecryptFileAsync(string srcFile, string destFile)
        {
            if (string.IsNullOrWhiteSpace(srcFile) || !File.Exists(srcFile))
            {
                _loggingService.Log(LogLevel.Warning, $"Invalid source file {srcFile}, skipping...");
                return;
            }

            using (var input = new VirtualFileStream(srcFile))
            using (var output = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 128000,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                output.SetLength(0);
                var buffer = await input.ReadAsync().ConfigureAwait(false);
                await output.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                _loggingService.Log(LogLevel.Information, $"Decrypted clip {Path.GetFileName(destFile)}.");
            }
        }

        /// <summary>
        ///     Copies the course image if one exists.
        /// </summary>
        private async Task CopyCourseImageAsync(string courseSource, string courseOutput)
        {
            string imageSrc = Path.Combine(courseSource, "image.jpg");
            string imageOutput = Path.Combine(courseOutput, "image.jpg");
            if (!File.Exists(imageSrc))
            {
                _loggingService.Log(LogLevel.Warning, $"No course image found in {courseSource}, skipping.");
                return;
            }
            if (!File.Exists(imageOutput))
            {
                using (var sourceStream = new FileStream(imageSrc, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var destinationStream = new FileStream(imageOutput, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
                    _loggingService.Log(LogLevel.Debug, $"Copied course image to {imageOutput}...");
                    return;
                }
            }
            _loggingService.Log(LogLevel.Debug, $"Course image already exists at {imageOutput}, skipping...");
        }

        private Task WriteCourseInfoAsync(Course courseInfo, string courseOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(courseInfo, Formatting.Indented);
            string output = Path.Combine(courseOutput, "course-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                _loggingService.Log(LogLevel.Debug,
                    $"Finished writing course info for {courseInfo.Name}...");
                return File.WriteAllTextAsync(output, serializedOutput);
            }
            _loggingService.Log(LogLevel.Warning, "Invalid course info, skipping...");
            return Task.CompletedTask;
        }

        private Task WriteModuleInfoAsync(Module moduleInfo, string moduleOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(moduleInfo, Formatting.Indented);
            string output = Path.Combine(moduleOutput, "module-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                _loggingService.Log(LogLevel.Debug,
                    $"Writing module info for {moduleInfo.Name}...");
                return File.WriteAllTextAsync(output, serializedOutput);
            }
            _loggingService.Log(LogLevel.Warning, "Invalid module info, skipping...");
            return Task.CompletedTask;
        }

        private Task WriteClipInfoAsync(IEnumerable<Clip> clipInfo, string clipOutput)
        {
            string serializedOutput = JsonConvert.SerializeObject(clipInfo, Formatting.Indented);
            string output = Path.Combine(clipOutput, "clip-info.json");
            if (!string.IsNullOrEmpty(serializedOutput))
            {
                _loggingService.Log(LogLevel.Debug,
                    $"Writing clip info for {clipInfo?.FirstOrDefault()?.Name}...");
                return File.WriteAllTextAsync(output, serializedOutput);
            }
            _loggingService.Log(LogLevel.Warning, "Invalid clip info, skipping...");
            return Task.CompletedTask;
        }
    }
}