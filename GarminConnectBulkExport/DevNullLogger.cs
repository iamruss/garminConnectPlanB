using System.Diagnostics;

namespace GarminConnectBulkExport
{
    public class DevNullLogger : ILogger
    {
        public void Log(string message)
        {
            //only log to the output console in debug builds
            Debug.WriteLine(message);
        }
    }
}