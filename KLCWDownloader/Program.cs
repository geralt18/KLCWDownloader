using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace KLCWDownloader
{
	class Program
	{
		static void Main(string[] args)
		{
			//Przydałoby się zrobić obsługę wielowątkowości
			int threads = Environment.ProcessorCount;
			ServicePointManager.DefaultConnectionLimit = threads;
			ServicePointManager.Expect100Continue = false;

         //Aby pobrać audycje potrzebne są trzy wartości aby wysłać poprawne żądanie (sectionId, tabId, boxInstanceId)
         //<div id='s_panel_allContent_32988_124778'>
         //z tego DIV'a mamy -> tabId = 124778; boxInstanceId = 32988; //ABC popkltury
         //sectionId = 9 -> do pobrania np. z głównego adresu audycji
         //można również podsłuchać żądanie wysyłane przez stronę do serwera w trakcie doczytywania kolejnej strony z audycjami
         //tam jest wysyłany json, z którego możemy wyciągnąć potrzebne informacje

         int pageCount = 20;
         bool onlyFirst = false;

         pageCount = int.Parse(ConfigurationManager.AppSettings["pages"]);
         _downloadPath = ConfigurationManager.AppSettings["path"];
         _jsonFilePath = Path.Combine(_downloadPath, _jsonFileName);

         int sectionId, categoryId, tabId, boxInstanceId;
         sectionId = int.Parse(ConfigurationManager.AppSettings["sectionId"]);
         categoryId = int.Parse(ConfigurationManager.AppSettings["categoryId"]);
         tabId = int.Parse(ConfigurationManager.AppSettings["tabId"]);
         boxInstanceId = int.Parse(ConfigurationManager.AppSettings["boxInstanceId"]);

			try {
				LoadArchivedMp3();
				Dictionary<string, Mp3File> downloadList = new Dictionary<string, Mp3File>();

				for (int i = 1; i <= pageCount; i++) {
					_logger.Trace("Przetwarzam stronę {0}", i);
					string tabContent = SendTabContentReq(sectionId, categoryId, tabId, boxInstanceId, i);

					foreach (string pageUrl in ProcescTabContent(tabContent)) {
						_logger.Trace("Przetwarzam artukuł {0}", pageUrl);
						string pageContent = SendPageContentReq(pageUrl);
						List<Mp3File> mp3List = new List<Mp3File>();
						if (onlyFirst)
							mp3List = ProcesPageContentOne(pageContent);
						else
							mp3List = ProcesPageContentAll(pageContent);

						foreach (var mp3 in mp3List) {
							if (!_archivedMp3.ContainsKey(mp3.Id))
								if (!downloadList.ContainsKey(mp3.Id))
									downloadList.Add(mp3.Id, mp3);
						}
					}
				}

				int fileCount = 1;
				foreach (var d in downloadList) {
					string name = string.Format("{0} {1}", d.Value.Date.ToString("yyyy-MM-dd"), string.IsNullOrWhiteSpace(d.Value.Desc)? d.Value.Name : d.Value.Desc);
					_logger.Trace("[{1}/{2}] Pobieram plik -> {0}", name, fileCount++, downloadList.Count);
					if (DownloadFile(_downloadPath, d.Value.Url, name)) {
						_archivedMp3.Add(d.Key, d.Value);
						SaveArchivedMp3();
					}
				}
			} catch (Exception ex) {
				_logger.Error(ex, "Nieoczekiwany błąd");
			}
		}

		static void LoadArchivedMp3()
		{
			if (!File.Exists(_jsonFilePath))
				return;
			_logger.Trace("Loading archived mp3 list");
			using (StreamReader file = File.OpenText(_jsonFilePath)) {
				JsonSerializer serializer = new JsonSerializer();
				_archivedMp3 = (Dictionary<string, Mp3File>)serializer.Deserialize(file, typeof(Dictionary<string, Mp3File>));
			}
		}

		static void SaveArchivedMp3()
		{
			if (!Directory.Exists(Path.GetDirectoryName(_jsonFilePath)))
				Directory.CreateDirectory(Path.GetDirectoryName(_jsonFilePath));

			_logger.Trace("Saving archived mp3 list");
			using (StreamWriter file = File.CreateText(_jsonFilePath)) {
				JsonSerializer serializer = new JsonSerializer();
				serializer.Serialize(file, _archivedMp3);
			}
		}

		static string SendTabContentReq(int section, int category, int tab, int boxInstance, int page)
		{
			var request = (HttpWebRequest)WebRequest.Create("https://www.polskieradio.pl/CMS/TemplateBoxesManagement/TemplateBoxTabContent.aspx/GetTabContent");

			string json = JsonConvert.SerializeObject(new TabContentRequestObject(section, category, tab, boxInstance, page));
			var data = Encoding.ASCII.GetBytes(json);

			request.Method = "POST";
			request.Accept = "application/json, text/javascript, */*; q=0.01";
			request.ContentType = "application/json; charset=utf-8";
			request.ContentLength = data.Length;

			using (var stream = request.GetRequestStream()) {
				stream.Write(data, 0, data.Length);
			}

			var response = (HttpWebResponse)request.GetResponse();

			return new StreamReader(response.GetResponseStream()).ReadToEnd();
		}

		static List<string> ProcescTabContent(string tabContent)
		{
			List<string> urls = new List<string>();
			TabContentResponseObject resp = JsonConvert.DeserializeObject<TabContentResponseObject>(tabContent);
			if (string.IsNullOrWhiteSpace(resp.d.Content))
				return urls;

         //<a href="/8/405/Artykul/1937044,Marian-Smoluchowski-Zapomniany-geniusz-fizyki" class="" title="Marian Smoluchowski. Zapomniany geniusz fizyki">
         Regex pattern = new Regex(@"<a href=""(?<url>[\w\d\s+-–.%#&/]+)""");

         MatchCollection mp1 = pattern.Matches(resp.d.Content);
			foreach (Match p in mp1) {
				string gname = p.Groups["url"].Value;
				urls.Add(WebUtility.HtmlDecode(gname));
			}

			return urls;
		}

		static string SendPageContentReq(string pageUrl)
		{
			var request = (HttpWebRequest)WebRequest.Create(new Uri(_baseUrl, pageUrl));
			var response = (HttpWebResponse)request.GetResponse();
			return new StreamReader(response.GetResponseStream()).ReadToEnd();
		}

		static List<Mp3File> ProcesPageContentAll(string pageContent)
		{
			//<span id="datetime2" class="time">
			//   23.07.2016 13:20
			//</span>
			List<DateTime> dtList = new List<DateTime>();
			Dictionary<string, MediaDataObject> dict = new Dictionary<string, MediaDataObject>();

			Regex pattern = new Regex(@"<span id=""datetime\d"" class=""time"">(?<date>[\d\s:.]+)<\/span>");
			MatchCollection mp1 = pattern.Matches(pageContent);
			if (mp1.Count > 0) {
				DateTime dt = DateTime.MinValue;
				string sDate = mp1[0].Groups["date"].Value?.Trim();
				DateTime.TryParseExact(sDate, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
				dtList.Add(dt);
			}

			//<span class="time">
			//        03.06.2017 18:00
			//</span>
			pattern = new Regex(@"<span class=""time"">(?<date>[\d\s:.]+)<\/span>");
			MatchCollection mp2 = pattern.Matches(pageContent);
			foreach (Match p in mp2) {
				DateTime dt = DateTime.MinValue;
				string sDate = p.Groups["date"].Value?.Trim();
				DateTime.TryParseExact(sDate, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
				dtList.Add(dt);
			}

			//<a title="odsłuchaj Rycheza - pierwsza królowa Polski" class="pr-media-play" data-media={"id":382196,"file":"//static.prsa.pl/815b7d33-8711-4675-afc7-356d510b37af.mp3","provider":"audio","uid":"815b7d33-8711-4675-afc7-356d510b37af","length":3477,"autostart":true,"link":"/8/405/Artykul/251896,Rycheza-pierwsza-krolowa-Polski","title":"Rycheza%20-%20pierwsza%20kr%C3%B3lowa%20Polski","desc":"Rycheza%20-%20pierwsza%20kr%C3%B3lowa%20Polski","advert":143762,"type":"muzyka"}>
			pattern = new Regex(@"data-media=(?<data>[\w\d\s:,.\-{}%"" /]+)>");
			MatchCollection mp3 = pattern.Matches(pageContent);
			foreach (Match p in mp3) {
				MediaDataObject media = JsonConvert.DeserializeObject<MediaDataObject>(p.Groups["data"].Value);
				//string f = Path.Combine(_downloadPath, media.id.ToString() + ".json");
				//File.WriteAllText(f, mp3[0].Groups["data"].Value);
				if (!dict.ContainsKey(media.file))
					dict.Add(media.file, media);
			}

			List<Mp3File> files = new List<Mp3File>();
			int i = 0;
			foreach (var d in dict)
				files.Add(new Mp3File() { Id = d.Value.uid, Date = dtList.Count <= i ? dtList[0] : dtList[i++], Name = WebUtility.UrlDecode(d.Value.title), Desc = WebUtility.UrlDecode(d.Value.desc), Url = d.Key });

			return files;
		}

		static List<Mp3File> ProcesPageContentOne(string pageContent)
		{
			//<span id="datetime2" class="time">
			//   23.07.2016 13:20
			//</span>
			List<DateTime> dtList = new List<DateTime>();
			List<Mp3File> files = new List<Mp3File>();
			Dictionary<string, MediaDataObject> dict = new Dictionary<string, MediaDataObject>();

			Regex pattern = new Regex(@"<span id=""datetime\d"" class=""time"">(?<date>[\d\s:.]+)<\/span>");
			MatchCollection mp1 = pattern.Matches(pageContent);
			if (mp1.Count > 0) {
				DateTime dt = DateTime.MinValue;
				string sDate = mp1[0].Groups["date"].Value?.Trim();
				DateTime.TryParseExact(sDate, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
				dtList.Add(dt);
			}

			//<a title="odsłuchaj Rycheza - pierwsza królowa Polski" class="pr-media-play" data-media={"id":382196,"file":"//static.prsa.pl/815b7d33-8711-4675-afc7-356d510b37af.mp3","provider":"audio","uid":"815b7d33-8711-4675-afc7-356d510b37af","length":3477,"autostart":true,"link":"/8/405/Artykul/251896,Rycheza-pierwsza-krolowa-Polski","title":"Rycheza%20-%20pierwsza%20kr%C3%B3lowa%20Polski","desc":"Rycheza%20-%20pierwsza%20kr%C3%B3lowa%20Polski","advert":143762,"type":"muzyka"}>
			pattern = new Regex(@"data-media=(?<data>[\w\d\s:,.\-{}%"" /]+)>");
			MatchCollection mp3 = pattern.Matches(pageContent);
			if (mp3.Count > 0) {
				MediaDataObject media = JsonConvert.DeserializeObject<MediaDataObject>(mp3[0].Groups["data"].Value);
				//string p = Path.Combine(_downloadPath, media.id.ToString() + ".json");
				//File.WriteAllText(p, mp3[0].Groups["data"].Value);
				files.Add(new Mp3File() { Id = media.uid, Date = dtList[0], Name = WebUtility.UrlDecode(media.title), Desc = WebUtility.UrlDecode(media.desc), Url = media.file });
			}

			return files;
		}

		static bool DownloadFile(string path, string url, string name)
		{
			bool ret = false;
			string filePath = Path.Combine(path, CleanFileName(name?.Trim()));
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);

			if (filePath.Length >= 250)
				filePath = filePath.Substring(0, 249);

			filePath += ".mp3";

			try {
				if (!File.Exists(filePath)) {
					WebClient wb = new WebClient();
					wb.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 6.1; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/46.0.2490.33 Safari/537.36");
					wb.DownloadFile(new Uri(_baseUrl, url), filePath);
					ret = true;
				} else
					_logger.Trace($"Pomijam plik -> {url}");
			} catch (Exception ex) {
				_logger.Error(ex, "Wystąpił błąd w trakcie pobierania pliku {0}", filePath);
				File.Delete(filePath);
			}
			return ret;
		}

		static string CleanFileName(string name)
		{
			foreach (char c in System.IO.Path.GetInvalidFileNameChars())
				name = name.Replace(c.ToString(), "");
			return name;
		}
		private static Dictionary<string, Mp3File> _archivedMp3 = new Dictionary<string, Mp3File>();
		private static Logger _logger = LogManager.GetCurrentClassLogger();
		private static Uri _baseUrl = new Uri("https://www.polskieradio.pl");
		private static string _downloadPath;
		private static string _jsonFilePath;
		private const string _jsonFileName = "mp3.json";


		public class Mp3File
		{
			public string Id { get; set; }
			public string Name { get; set; }
			public string Desc { get; set; }
			public string Url { get; set; }
			public DateTime Date { get; set; }
		}

		public class TabContentResponseObject
		{
			public D d { get; set; }
		}

		public class D
		{
			public string __type { get; set; }
			public string Content { get; set; }
			public string PagerContent { get; set; }
			public object FeedLink { get; set; }
		}

		public class MediaDataObject
		{
			public int id { get; set; }
			public string file { get; set; }
			public string provider { get; set; }
			public string uid { get; set; }
			public int length { get; set; }
			public bool autostart { get; set; }
			public string link { get; set; }
			public string title { get; set; }
			public string desc { get; set; }
			public int advert { get; set; }
			public string type { get; set; }
		}

		public class TabContentRequestObject
		{
			public int tabId { get; set; }
			public int boxInstanceId { get; set; }
			public int sectionId { get; set; }
			public int categoryId { get; set; }
			public int categoryType { get; set; }
			public string subjectIds { get; set; }
			public int tagIndexId { get; set; }
			public string queryString { get; set; }
			public string name { get; set; }
			public int pageNumber { get; set; }
			public int pagerMode { get; set; }
			public string openArticlesInParentTemplate { get; set; }
			public int idSectionFromUrl { get; set; }
			public int maxDocumentAge { get; set; }
			public string showCategoryForArticle { get; set; }

			public TabContentRequestObject(int section, int cat, int tab, int boxInstance, int page)
			{
            sectionId = section;
            //categoryId = cat;
            tabId = tab;
            boxInstanceId = boxInstance;
            categoryType = 0;
				subjectIds = "";
				tagIndexId = 444;
				queryString = string.Empty;
				//queryString = string.Format("stid={0}&ctid={1}&par=Artykul", section, cat);
				name = "";
				pageNumber = page;
				pagerMode = 0;
				openArticlesInParentTemplate = "True";
				idSectionFromUrl = section;
				maxDocumentAge = 6000;
				showCategoryForArticle = "False";
			}
		}
	}
}

