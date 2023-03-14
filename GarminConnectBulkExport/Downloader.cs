using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Newtonsoft.Json.Linq;

namespace GarminConnectBulkExport
{
    public class Downloader
    {
        private readonly CookieContainer _cookieJar;
        private readonly string _email;
        private readonly string _folder;
        private readonly string _password;
        private int _redirectCount;

        public Downloader(string email, string password, string folder, ILogger logger)
        {
            _email = email ?? throw new ArgumentNullException(nameof(email));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cookieJar = new CookieContainer();
        }

        private ILogger Logger { get; }

        private string DoGet(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.CookieContainer = _cookieJar;
                request.AllowAutoRedirect = false;
                request.Referer = "https://connect.garmin.com/modern/";
                request.Method = "GET";
                var response = (HttpWebResponse)request.GetResponse();


                if ((int)response.StatusCode > 300 && (int)response.StatusCode < 399)
                {
                    string redirectUrl = response.Headers["Location"];
                    Logger.Log("Redirected to: " + redirectUrl);
                    _redirectCount++;
                    if (_redirectCount <= 6)
                    {
                        return DoGet(redirectUrl);
                    }
                }

                Logger.Log($"Content length is {response.ContentLength}");
                Logger.Log($"Content type is {response.ContentType}");

                // Get the stream associated with the response.
                var receiveStream = response.GetResponseStream();

                var res = string.Empty;

                if (receiveStream != null)
                {
                    var readStream = new StreamReader(receiveStream, Encoding.UTF8);
                    res = readStream.ReadToEnd();
                    readStream.Close();
                }

                response.Close();
                return res;
            }
            catch (WebException webex)
            {
                return ((HttpWebResponse)(webex.Response)).StatusCode == HttpStatusCode.NotFound ? "OK" : string.Empty;
            }
        }

        private string DoPost(string url, NameValueCollection data)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.AllowAutoRedirect = false;
            request.CookieContainer = _cookieJar;

            var sb = new StringBuilder();
            foreach (var key in data.AllKeys)
            {
                sb.AppendFormat("{0}={1}&", key, HttpUtility.UrlEncode(data[key]));
            }

            sb.Remove(sb.Length - 1, 1);

            var byteArray = Encoding.UTF8.GetBytes(sb.ToString());
            request.AllowAutoRedirect = false;
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded;charset=UTF-8";
            request.Referer = "https://connect.garmin.com/modern/";

            //request.Credentials = CredentialCache.DefaultCredentials;
            request.ContentLength = byteArray.Length;

            using (var dataStream = request.GetRequestStream())
            {
                dataStream.Write(byteArray, 0, byteArray.Length);
                dataStream.Close();
            }

            var response = (HttpWebResponse)request.GetResponse();
            Console.WriteLine("Content length is {0}", response.ContentLength);
            Console.WriteLine("Content type is {0}", response.ContentType);

            // Get the stream associated with the response.
            var receiveStream = response.GetResponseStream();

            var res = string.Empty;
            if (receiveStream != null)
            {
                using (var readStream = new StreamReader(receiveStream, Encoding.UTF8))
                {
                    res = readStream.ReadToEnd();
                    readStream.Close();
                }
            }

            response.Close();

            return res;
        }

        private bool Login()
        {
            Logger.Log("Starting Auth...");
            var loginParams = new NameValueCollection();
            var resultStr = DoGet("https://sso.garmin.com/sso/login?service="
                                  + HttpUtility.UrlEncode("https://connect.garmin.com/post-auth/login")
                                  + "&clientId=GarminConnect&consumeServiceTicket=false");

            var ltRegex = new Regex(@"name=""lt""\s+value=""([^""]+)""");
            var ltMatch = ltRegex.Match(resultStr);
            if (!ltMatch.Success || ltMatch.Groups.Count != 2) return false;
            var lt = ltMatch.Groups[1].Value;
            loginParams.Add("lt", lt);

            Logger.Log("Obtained lt..." + lt);

            loginParams.Add("username", _email);
            loginParams.Add("password", _password);
            loginParams.Add("_eventId", "submit");
            loginParams.Add("embed", "true");

            resultStr = DoPost("https://sso.garmin.com/sso/login?service="
                               + HttpUtility.UrlEncode("https://connect.garmin.com/post-auth/login")
                               + "&clientId=GarminConnect&consumeServiceTicket=false&lt=" + lt, loginParams);

            var ticketRegex = new Regex(@"ticket=([^']+)'");
            var ticketMatch = ticketRegex.Match(resultStr);
            if (!ticketMatch.Success || ticketMatch.Groups.Count != 2) return false;

            var ticket = ticketMatch.Groups[1].Value;
            Logger.Log("Obtained ticket..." + ticket);
            _redirectCount = 0;
            resultStr =
                DoGet("https://connect.garmin.com/post-auth/login?ticket=" +
                      HttpUtility.UrlEncode(ticket.Trim()));

            return !string.IsNullOrEmpty(resultStr);
        }

        public void Download()
        {
            if (!Login()) return;

            Logger.Log("Logged in...");
            var pageNumber = 1;
            const int pageSize = 100; //seems to be the max possible amount of results per api call
            IJEnumerable<JToken> list;
            do
            {
                var start = (pageNumber - 1) * pageSize;
                Logger.Log($"Downloading list of available activities. Page #{pageNumber}");
                var activities =
                    DoGet(
                        $"https://connect.garmin.com/modern/proxy/activity-search-service-1.0/json/activities?start={start}&limit={pageSize}");

                var results = JObject.Parse(activities);

                list = results.GetValue("results")?["activities"]?.AsJEnumerable() ?? null;
                if (list != null)
                {
                    foreach (var token in list)
                    {
                        var activityId = token["activity"]?["activityId"]?.ToString() ?? string.Empty;
                        Logger.Log("Downloading activity " + activityId);
                        DownloadActivity(activityId);
                    }
                }

                pageNumber++;
            } while (list != null);
        }

        private void DownloadActivity(string activityId)
        {
            var fileName = $"{_folder}\\{activityId}.zip";
            if (File.Exists(fileName))
            {
                Logger.Log($"File \"{fileName}\" already exists");
                return;
            }

            try
            {
                var request =
                    (HttpWebRequest)
                    WebRequest.Create($"https://connect.garmin.com/proxy/download-service/files/activity/{activityId}");
                request.CookieContainer = _cookieJar;
                request.AllowAutoRedirect = false;
                request.Referer = "https://connect.garmin.com/modern/";
                request.Method = "GET";
                var response = (HttpWebResponse)request.GetResponse();

                Logger.Log($"Content length is {response.ContentLength}");
                Logger.Log($"Content type is {response.ContentType}");


                using (var stream = File.Create(fileName))
                {
                    var responseStream = response.GetResponseStream();
                    responseStream?.CopyTo(stream);

                    stream.Close();
                }

                response.Close();
            }
            catch (WebException webex)
            {
                if (((HttpWebResponse)(webex.Response)).StatusCode == HttpStatusCode.NotFound)
                {
                    Logger.Log($"Activity {activityId} could not be downloaded");
                }
            }
        }
    }
}