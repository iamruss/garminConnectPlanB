using System;
using System.IO;

namespace GarminConnectBulkExport
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            string userName = string.Empty;
            string password = string.Empty;
            string outputDir = string.Empty;

            foreach (string arg in args)
            {
                if (arg == "-p")
                {
                    password = AskPassword();
                }
                else if (arg == "-u")
                {
                    userName = AskUserName();
                }
                else
                {
                    string[] pair = arg.Split('=');
                    if (pair.Length != 2)
                    {
                        Console.WriteLine("Invalid Parameter passed: {0}", arg);
                    }
                    else
                    {
                        switch (pair[0])
                        {
                            case "-u":
                                userName = pair[1];
                                break;
                            case "-p":
                                password = pair[1];
                                break;
                            case "-o":
                                outputDir = pair[1];
                                break;
                            default:
                                Console.WriteLine("Unknown parameter passed: {0}", pair[0]);
                                break;
                        }
                    }
                }
            }

            if (!Directory.Exists(outputDir))
            {
                Console.WriteLine("Cannot access {0}", outputDir);
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                password = AskPassword();
            }

            if (string.IsNullOrEmpty(userName))
            {
                userName = AskUserName();
            }

            var logger = new ConsoleLogger();
            var downloader = new Downloader(userName, password, outputDir, logger);
            downloader.Download();

            logger.Log("OPERATION COMPLETED!");
        }

        private static string AskUserName()
        {
            string userName;
            do
            {
                Console.Write("Enter Garmin Connect username(email):");
                userName = Console.ReadLine();
            } while (userName == null);

            userName = userName.Trim();
            return userName;
        }

        private static string AskPassword()
        {
            string password;
            do
            {
                Console.Write("Enter Garmin Connect password:");
                password = Console.ReadLine();
            } while (password == null);

            password = password.Trim();
            return password;
        }
    }
}