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
        private readonly ILogger _logger;
        private readonly string _password;
        private int _redirectCount;

        public Downloader(string email, string password, string folder, ILogger logger)
        {
            if (email == null) throw new ArgumentNullException("email");
            if (password == null) throw new ArgumentNullException("password");
            if (folder == null) throw new ArgumentNullException("folder");
            if (logger == null) throw new ArgumentNullException("logger");

            _email = email;
            _password = password;
            _folder = folder;
            _logger = logger;
            _cookieJar = new CookieContainer();
        }

        public ILogger Logger
        {
            get { return _logger; }
        }

        public string DoGet(string url)
        {
            try
            {
                var request = (HttpWebRequest) WebRequest.Create(url);
                request.CookieContainer = _cookieJar;
                request.AllowAutoRedirect = false;
                request.Referer = "https://connect.garmin.com/modern/";
                request.Method = "GET";
                var response = (HttpWebResponse) request.GetResponse();


                if ((int) response.StatusCode > 300 && (int) response.StatusCode < 399)
                {
                    string redirectUrl = response.Headers["Location"];
                    Logger.Log("Redirected to: " + redirectUrl);
                    _redirectCount++;
                    if (_redirectCount <= 6)
                    {
                        return DoGet(redirectUrl);
                    }
                }

                Logger.Log(string.Format("Content length is {0}", response.ContentLength));
                Logger.Log(string.Format("Content type is {0}", response.ContentType));

                // Get the stream associated with the response.
                Stream receiveStream = response.GetResponseStream();

                string res = string.Empty;

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
                if (((HttpWebResponse) (webex.Response)).StatusCode == HttpStatusCode.NotFound)
                {
                    return "OK";
                }
                return string.Empty;
            }
        }

        public string DoPost(string url, NameValueCollection data)
        {
            var request = (HttpWebRequest) WebRequest.Create(url);
            request.AllowAutoRedirect = false;
            request.CookieContainer = _cookieJar;

            var sb = new StringBuilder();
            foreach (string key in data.AllKeys)
            {
                sb.AppendFormat("{0}={1}&", key, HttpUtility.UrlEncode(data[key]));
            }
            sb.Remove(sb.Length - 1, 1);

            byte[] byteArray = Encoding.UTF8.GetBytes(sb.ToString());
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

            var response = (HttpWebResponse) request.GetResponse();
            Console.WriteLine("Content length is {0}", response.ContentLength);
            Console.WriteLine("Content type is {0}", response.ContentType);

            // Get the stream associated with the response.
            Stream receiveStream = response.GetResponseStream();

            string res = string.Empty;
            if (receiveStream != null)
            {
                using(var readStream = new StreamReader(receiveStream, Encoding.UTF8))
                {
                    res = readStream.ReadToEnd();
                    readStream.Close();
                }
            }
            response.Close();

            return res;

        }

        public bool Login()
        {
            Logger.Log("Starting Auth...");
            var loginParams = new NameValueCollection();
            string resultStr = DoGet("https://sso.garmin.com/sso/login?service="
                                     + HttpUtility.UrlEncode("https://connect.garmin.com/post-auth/login")
                                     + "&clientId=GarminConnect&consumeServiceTicket=false");

            var ltRegex = new Regex(@"name=""lt""\s+value=""([^""]+)""");
            Match ltMatch = ltRegex.Match(resultStr);
            if (ltMatch.Success && ltMatch.Groups.Count == 2)
            {
                string lt = ltMatch.Groups[1].Value;
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
                Match ticketMatch = ticketRegex.Match(resultStr);
                if (ticketMatch.Success && ticketMatch.Groups.Count == 2)
                {
                    string ticket = ticketMatch.Groups[1].Value;
                    Logger.Log("Obtained ticket..." + ticket);
                    _redirectCount = 0;
                    resultStr =
                        DoGet("https://connect.garmin.com/post-auth/login?ticket=" +
                              HttpUtility.UrlEncode(ticket.Trim()));

                    return !string.IsNullOrEmpty(resultStr);
                }
            }

            return false;
        }

        public void Download()
        {
            if (Login())
            {
                Logger.Log("Logged in...");
                int pageNumber = 1;
                const int pageSize = 100; //seems to be the max possible amount of results per api call
                IJEnumerable<JToken> list;
                do
                {
                    int start = (pageNumber - 1)*pageSize;
                    Logger.Log(string.Format("Downloading list of available activities. Page #{0}", pageNumber));
                    string activities =
                        DoGet(
                            string.Format(
                                "https://connect.garmin.com/modern/proxy/activity-search-service-1.0/json/activities?start={0}&limit={1}",
                                start, pageSize));

                    JObject results = JObject.Parse(activities);

                    list = results.GetValue("results")["activities"].AsJEnumerable();
                    if (list != null)
                    {
                        foreach (JToken token in list)
                        {
                            string activityId = token["activity"]["activityId"].ToString();
                            Logger.Log("Downloading activity " + activityId);
                            DownloadActivity(activityId);
                        }
                    }
                    pageNumber++;
                } while (list != null);
            }
        }

        private void DownloadActivity(string activityId)
        {
            string fileName = string.Format("{0}\\{1}.zip", _folder, activityId);
            if (File.Exists(fileName))
            {
                Logger.Log(string.Format("File \"{0}\" already exists", fileName));
                return;
            }
            try
            {
                var request =
                    (HttpWebRequest)
                        WebRequest.Create(
                            string.Format("https://connect.garmin.com/proxy/download-service/files/activity/{0}", activityId));
                request.CookieContainer = _cookieJar;
                request.AllowAutoRedirect = false;
                request.Referer = "https://connect.garmin.com/modern/";
                request.Method = "GET";
                var response = (HttpWebResponse) request.GetResponse();

                Logger.Log(string.Format("Content length is {0}", response.ContentLength));
                Logger.Log(string.Format("Content type is {0}", response.ContentType));


                using (FileStream stream = File.Create(fileName))
                {
                    var responseStream = response.GetResponseStream();
                    if (responseStream != null)
                    {
                        responseStream.CopyTo(stream);
                    }
                    stream.Close();
                }
                response.Close();
            }
            catch (WebException webex)
            {
                if (((HttpWebResponse)(webex.Response)).StatusCode == HttpStatusCode.NotFound)
                {
                    Logger.Log(string.Format("Activity {0} could not be downloaded", activityId));
                }
            }
        }
    }
}