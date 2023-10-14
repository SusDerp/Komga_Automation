﻿using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Tranga.Jobs;

namespace Tranga.MangaConnectors;

public class Bato : MangaConnector
{
	public Bato(GlobalBase clone) : base(clone, "Bato")
	{
		this.downloadClient = new HttpDownloadClient(clone, new Dictionary<byte, int>()
		{
			{1, 60}
		});
	}

	public override Manga[] GetManga(string publicationTitle = "")
	{
		Log($"Searching Publications. Term=\"{publicationTitle}\"");
		string sanitizedTitle = string.Join(' ', Regex.Matches(publicationTitle, "[A-z]*").Where(m => m.Value.Length > 0)).ToLower();
		string requestUrl = $"https://bato.to/v3x-search?word={sanitizedTitle}&lang=en";
		DownloadClient.RequestResult requestResult =
			downloadClient.MakeRequest(requestUrl, 1);
		if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
			return Array.Empty<Manga>();

		if (requestResult.htmlDocument is null)
		{
			Log($"Failed to retrieve site");
			return Array.Empty<Manga>();
		}
			
		Manga[] publications = ParsePublicationsFromHtml(requestResult.htmlDocument);
		Log($"Retrieved {publications.Length} publications. Term=\"{publicationTitle}\"");
		return publications;
	}

	public override Manga? GetMangaFromUrl(string url)
	{
		DownloadClient.RequestResult requestResult =
			downloadClient.MakeRequest(url, 1);
		if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
			return null;
		if (requestResult.htmlDocument is null)
		{
			Log($"Failed to retrieve site");
			return null;
		}
		return ParseSinglePublicationFromHtml(requestResult.htmlDocument, url.Split('/')[^1]);
	}

	private Manga[] ParsePublicationsFromHtml(HtmlDocument document)
	{
		HtmlNode mangaList = document.DocumentNode.SelectSingleNode("//div[@data-hk='0-0-2']");
		if (!mangaList.ChildNodes.Any(node => node.Name == "div"))
			return Array.Empty<Manga>();

		List<string> urls = mangaList.ChildNodes
			.Select(node => $"https://bato.to{node.Descendants("div").First().FirstChild.GetAttributeValue("href", "")}").ToList();
		
		HashSet<Manga> ret = new();
		foreach (string url in urls)
		{
			Manga? manga = GetMangaFromUrl(url);
			if (manga is not null)
				ret.Add((Manga)manga);
		}

		return ret.ToArray();
	}

	private Manga ParseSinglePublicationFromHtml(HtmlDocument document, string publicationId)
	{
		HtmlNode infoNode = document.DocumentNode.SelectSingleNode("/html/body/div/main/div[1]/div[2]");

		string sortName = infoNode.Descendants("h3").First().InnerText;
		string description = document.DocumentNode
			.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),'prose')]").InnerText;

		string[] altTitlesList = infoNode.ChildNodes[1].ChildNodes[2].InnerText.Split('/');
		int i = 0;
		Dictionary<string, string> altTitles = altTitlesList.ToDictionary(s => i++.ToString(), s => s);

		string posterUrl = document.DocumentNode.SelectNodes("//img")
			.First(child => child.GetAttributeValue("data-hk", "") == "0-1-0").GetAttributeValue("src", "").Replace("&amp;", "&");
		string coverFileNameInCache = SaveCoverImageToCache(posterUrl, 1);

		List<HtmlNode> genreNodes = document.DocumentNode.SelectSingleNode("//b[text()='Genres:']/..").SelectNodes("span").ToList();
		string[] tags = genreNodes.Select(node => node.FirstChild.InnerText).ToArray();

		List<HtmlNode> authorsNodes = infoNode.ChildNodes[1].ChildNodes[3].Descendants("a").ToList();
		List<string> authors = authorsNodes.Select(node => node.InnerText.Replace("amp;", "")).ToList();

		HtmlNode? originalLanguageNode = document.DocumentNode.SelectSingleNode("//span[text()='Tr From']/..");
		string originalLanguage = originalLanguageNode is not null ? originalLanguageNode.LastChild.InnerText : "";

		if (!int.TryParse(
			    document.DocumentNode.SelectSingleNode("//span[text()='Original Publication:']/..").LastChild.InnerText.Split('-')[0],
			    out int year))
			year = DateTime.Now.Year;

		string status = document.DocumentNode.SelectSingleNode("//span[text()='Original Publication:']/..")
			.ChildNodes[2].InnerText;

		Manga manga = new (sortName, authors, description, altTitles, tags, posterUrl, coverFileNameInCache, new Dictionary<string, string>(),
			year, originalLanguage, status, publicationId);
		cachedPublications.Add(manga);
		return manga;
	}

	public override Chapter[] GetChapters(Manga manga, string language="en")
	{
		Log($"Getting chapters {manga}");
		string requestUrl = $"https://bato.to/title/{manga.publicationId}";
		// Leaving this in for verification if the page exists
		DownloadClient.RequestResult requestResult =
			downloadClient.MakeRequest(requestUrl, 1);
		if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
			return Array.Empty<Chapter>();

		//Return Chapters ordered by Chapter-Number
		List<Chapter> chapters = ParseChaptersFromHtml(manga, requestUrl);
		Log($"Got {chapters.Count} chapters. {manga}");
		return chapters.OrderBy(chapter => Convert.ToSingle(chapter.chapterNumber, numberFormatDecimalPoint)).ToArray();
	}

	private List<Chapter> ParseChaptersFromHtml(Manga manga, string mangaUrl)
	{
		// Using HtmlWeb will include the chapters since they are loaded with js 
		HtmlWeb web = new();
		HtmlDocument document = web.Load(mangaUrl);

		List<Chapter> ret = new();

		HtmlNode chapterList =
			document.DocumentNode.SelectSingleNode("/html/body/div/main/div[3]/astro-island/div/div[2]/div/div/astro-slot");

		Regex chapterNumberRex = new(@"Chapter ([0-9\.]+)");

		foreach (HtmlNode chapterInfo in chapterList.SelectNodes("div"))
		{
			HtmlNode infoNode = chapterInfo.FirstChild.FirstChild;
			string fullString = infoNode.InnerText;

			string? volumeNumber = null;
			string chapterNumber = chapterNumberRex.Match(fullString).Groups[1].Value;
			string chapterName = chapterNumber;
			string url = $"https://bato.to{infoNode.GetAttributeValue("href", "")}?load=2";
			ret.Add(new Chapter(manga, chapterName, volumeNumber, chapterNumber, url));
		}
		
		return ret;
	}

	public override HttpStatusCode DownloadChapter(Chapter chapter, ProgressToken? progressToken = null)
	{
		if (progressToken?.cancellationRequested ?? false)
		{
			progressToken.Cancel();
			return HttpStatusCode.RequestTimeout;
		}

		Manga chapterParentManga = chapter.parentManga;
		Log($"Retrieving chapter-info {chapter} {chapterParentManga}");
		string requestUrl = chapter.url;
		// Leaving this in to check if the page exists
		DownloadClient.RequestResult requestResult =
			downloadClient.MakeRequest(requestUrl, 1);
		if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
		{
			progressToken?.Cancel();
			return requestResult.statusCode;
		}

		string[] imageUrls = ParseImageUrlsFromHtml(requestUrl);

		string comicInfoPath = Path.GetTempFileName();
		File.WriteAllText(comicInfoPath, chapter.GetComicInfoXmlString());

		return DownloadChapterImages(imageUrls, chapter.GetArchiveFilePath(settings.downloadLocation), 1, comicInfoPath, "https://mangakatana.com/", progressToken:progressToken);
	}

	private string[] ParseImageUrlsFromHtml(string mangaUrl)
	{
		DownloadClient.RequestResult requestResult =
			downloadClient.MakeRequest(mangaUrl, 1);
		if ((int)requestResult.statusCode < 200 || (int)requestResult.statusCode >= 300)
		{
			return Array.Empty<string>();
		}
		if (requestResult.htmlDocument is null)
		{
			Log($"Failed to retrieve site");
			return Array.Empty<string>();
		}

		HtmlDocument document = requestResult.htmlDocument;

		HtmlNode images = document.DocumentNode.SelectNodes("//astro-island").First(node =>
			node.GetAttributeValue("component-url", "").Contains("/_astro/ImageList."));

		string weirdString = images.OuterHtml;
		string weirdString2 = Regex.Match(weirdString, @"props=\""(.*)}\""").Groups[1].Value;
		string[] urls = Regex.Matches(weirdString2, @"https:\/\/[A-z\-0-9\.\?\&\;\=\/]*").Select(m => m.Value.Replace("\\&quot;]", "").Replace("amp;", "")).ToArray();
		
		return urls;
	}
}