﻿namespace Redpoint.Uet.BuildPipeline.Executors
{
    using Microsoft.Extensions.Logging;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    public class LoggerBasedBuildExecutionEvents : IBuildExecutionEvents
    {
        private readonly ILogger _logger;
        private static readonly Regex _warningRegex = new Regex("([^a-zA-Z0-9]|^)([Ww]arning)([^a-zA-Z0-9]|$)");
        private static readonly Regex _errorRegex = new Regex("([^a-zA-Z0-9]|^)([Ee]rror)([^a-zA-Z0-9]|$)");
        private static readonly Regex _successfulRegex = new Regex("([^a-zA-Z0-9]|^)([Ss][Uu][Cc][Cc][Ee][Ss][Ss][Ff]?[Uu]?[Ll]?)([^a-zA-Z0-9]|$)");
        private static readonly Regex _uetInfoRegex = new Regex("([0-9][0-9]:[0-9][0-9]:[0-9][0-9] \\[)(info?)(\\])");
        private static readonly Regex _uetWarnRegex = new Regex("([0-9][0-9]:[0-9][0-9]:[0-9][0-9] \\[)(warn?)(\\])");
        private static readonly Regex _uetFailRegex = new Regex("([0-9][0-9]:[0-9][0-9]:[0-9][0-9] \\[)(fail?)(\\])");

        private readonly Dictionary<string, BuildResultStatus> _buildResults = new Dictionary<string, BuildResultStatus>();
        private readonly List<string> _buildResultsOrder = new List<string>();

        public LoggerBasedBuildExecutionEvents(ILogger logger)
        {
            _logger = logger;
        }

        public List<(string nodeName, BuildResultStatus resultStatus)> GetResults()
        {
            var results = new List<(string nodeName, BuildResultStatus resultStatus)>();
            foreach (var nodeName in _buildResultsOrder)
            {
                results.Add((nodeName, _buildResults.ContainsKey(nodeName) ? _buildResults[nodeName] : BuildResultStatus.NotRun));
            }
            return results;
        }

        public Task OnNodeFinished(string nodeName, BuildResultStatus resultStatus)
        {
            if (nodeName == "End")
            {
                return Task.CompletedTask;
            }
            _buildResults[nodeName] = resultStatus;
            switch (resultStatus)
            {
                case BuildResultStatus.Success:
                    _logger.LogInformation($"[{nodeName}] \x001B[32mPassed\x001B[0m");
                    break;
                case BuildResultStatus.Failed:
                    _logger.LogInformation($"[{nodeName}] \x001B[31mFailed\x001B[0m");
                    break;
                case BuildResultStatus.Cancelled:
                    _logger.LogInformation($"[{nodeName}] \x001B[33mCancelled\x001B[0m");
                    break;
                case BuildResultStatus.NotRun:
                    _logger.LogInformation($"[{nodeName}] \x001B[36mNot Run\x001B[0m");
                    break;
            }
            return Task.CompletedTask;
        }

        public Task OnNodeOutputReceived(string nodeName, string[] lines)
        {
            foreach (var line in lines)
            {
                var highlightedLine = _warningRegex.Replace(line, m => $"{m.Groups[1].Value}\u001b[33m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                highlightedLine = _errorRegex.Replace(highlightedLine, m => $"{m.Groups[1].Value}\u001b[31m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                highlightedLine = _successfulRegex.Replace(highlightedLine, m => $"{m.Groups[1].Value}\u001b[32m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                highlightedLine = _uetInfoRegex.Replace(highlightedLine, m => $"{m.Groups[1].Value}\u001b[32m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                highlightedLine = _uetWarnRegex.Replace(highlightedLine, m => $"{m.Groups[1].Value}\u001b[33m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                highlightedLine = _uetFailRegex.Replace(highlightedLine, m => $"{m.Groups[1].Value}\u001b[31m{m.Groups[2].Value}\u001b[0m{m.Groups[3].Value}");
                _logger.LogInformation($"[{nodeName}] {highlightedLine}");
            }
            return Task.CompletedTask;
        }

        public Task OnNodeStarted(string nodeName)
        {
            if (nodeName == "End")
            {
                return Task.CompletedTask;
            }
            _logger.LogInformation($"[{nodeName}] \x001B[35mStarting...\x001B[0m");
            if (!_buildResultsOrder.Contains(nodeName))
            {
                _buildResultsOrder.Add(nodeName);
            }
            return Task.CompletedTask;
        }
    }
}