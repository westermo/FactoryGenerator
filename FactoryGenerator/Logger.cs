using System;
using System.Diagnostics;
using System.IO;

namespace FactoryGenerator
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        None = 5
    }

    public interface ILogger
    {
        bool IsEnabled(LogLevel logLevel);
        void Log(LogLevel logLevel, string message);
    }

// does what it supposed to do - nothing
    public class NullLogger : ILogger
    {
        public static readonly ILogger Instance = new NullLogger();

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log(LogLevel logLevel, string message)
        {
        }
    }

    public class Logger : ILogger
    {
#pragma warning disable RS1035
        private readonly string m_fileName;
        private readonly LogLevel m_logLevel;

        public Logger(string fileName, LogLevel logLevel)
        {
            m_fileName = fileName;
            m_logLevel = logLevel;
            if(File.Exists(fileName)) File.Delete(fileName);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= m_logLevel;
        }

        public void Log(LogLevel logLevel, string message)
        {
            if (!IsEnabled(logLevel))
                return;

            File.AppendAllText(m_fileName, $"[{DateTime.Now:O} | {logLevel}] {message}{Environment.NewLine}");
#pragma warning restore RS1035
        }
    }
}