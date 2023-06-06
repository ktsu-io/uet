﻿namespace Redpoint.UET.Automation.TestReporter
{
    using Redpoint.UET.Automation.Model;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    public interface ITestReporter
    {
        Task ReportResultsAsync(TestResult[] results);
    }
}
