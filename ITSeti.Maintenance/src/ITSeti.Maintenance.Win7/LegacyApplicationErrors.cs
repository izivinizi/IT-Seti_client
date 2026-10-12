using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
namespace ITSeti.Maintenance.Win7
{
    internal sealed class LegacyApplicationError
    {
        public string Id { get; set; }
        public string OccurredAt { get; set; }
        public string Component { get; set; }
        public string Operation { get; set; }
        public string Summary { get; set; }
        public string ExceptionType { get; set; }
        public string TechnicalDetails { get; set; }
    }
    internal static class LegacyApplicationErrors
    {
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,DateTime> Recent=new Dictionary<string,DateTime>();
        internal static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ITSeti","Maintenance","Logs");
        internal static readonly string PathName=Path.Combine(Root,"application-errors-win7.jsonl");
        internal static void Record(string component,string operation,Exception exception)
        {
            try { lock(Gate) {
                var summary=Redact(exception.Message); var key=component+"|"+operation+"|"+summary; DateTime previous;
                if(Recent.TryGetValue(key,out previous)&&DateTime.UtcNow-previous<TimeSpan.FromMinutes(5))return;
                var entry=new LegacyApplicationError{Id=Guid.NewGuid().ToString("N"),OccurredAt=DateTimeOffset.UtcNow.ToString("O"),Component=component,Operation=operation,Summary=summary,ExceptionType=exception.GetType().FullName,TechnicalDetails=Redact(exception.ToString())};
                Directory.CreateDirectory(Root);
                File.AppendAllText(PathName,new JavaScriptSerializer().Serialize(entry)+Environment.NewLine,new UTF8Encoding(false));
                Recent[key]=DateTime.UtcNow;
                if(new FileInfo(PathName).Length>2*1024*1024)File.WriteAllLines(PathName,File.ReadAllLines(PathName).Reverse().Take(500).Reverse().ToArray(),new UTF8Encoding(false));
            }}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        internal static LegacyApplicationError[] Read()
        {
            try {if(!File.Exists(PathName))return new LegacyApplicationError[0];var entries=new List<LegacyApplicationError>();
                foreach(var line in File.ReadAllLines(PathName).Reverse().Take(100))try{var item=new JavaScriptSerializer().Deserialize<LegacyApplicationError>(line);if(item!=null)entries.Add(item);}catch(ArgumentException){}
                return entries.ToArray();
            }catch(IOException){return new LegacyApplicationError[0];}catch(UnauthorizedAccessException){return new LegacyApplicationError[0];}
        }
        private static string Redact(string text)
        {
            text=Regex.Replace(text??"","(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+","Bearer <скрыто>");
            text=Regex.Replace(text,"(?i)(password|пароль|authorization|device[-_ ]?key|token)[\\\"']?\\s*[:=]\\s*(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)","$1=<скрыто>");
            return text.Length>16000?text.Substring(0,16000):text;
        }
    }
}
