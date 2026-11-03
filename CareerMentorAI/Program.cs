using CareerMentor.Api.Infra;
using CareerMentorAI;
using CareerMentorAI.Helpers;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

string extractModel = builder.Configuration["OpenAI:ExtractModel"] ?? "gpt-4o-mini";
string synthModel = builder.Configuration["OpenAI:SynthModel"] ?? "gpt-4o-mini";
string apiKey = builder.Configuration["OPENAI_API_KEY"] ?? builder.Configuration["OpenAI:ApiKey"] ?? "";
int tpmLimit = int.TryParse(builder.Configuration["OpenAI:TPM"], out var tpm) ? tpm : 30000;
int maxCharsHardCap = int.TryParse(builder.Configuration["Analyze:MaxChars"], out var mc) ? mc : 250_000;

var kernelBuilder = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion(modelId: extractModel, apiKey: apiKey, serviceId: "extract")
    .AddOpenAIChatCompletion(modelId: synthModel, apiKey: apiKey, serviceId: "synth");
var kernel = kernelBuilder.Build();
builder.Services.AddSingleton(kernel);

builder.Services.AddSingleton(new TokenRateLimiter(tpmLimit));
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

builder.Services.AddDbContextPool<Context>(opt =>
{
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
});
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

string[] allowedOrigins = {
    "https://careermentor-ai.vercel.app",
    "http://localhost:3000" // opcional p/ dev
};
builder.Services.AddCors(o =>
{
    o.AddPolicy("CorsPolicy", p =>
        p.WithOrigins(allowedOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()
    );
});

var app = builder.Build();
app.UseResponseCompression();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("CorsPolicy");

app.MapGet("/", () => Results.Ok(new { ok = true, service = "CareerMentor JSON API" }));

app.MapMethods("/api/health/db", new[] { "GET", "HEAD" }, async (Context db, CancellationToken ct) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        return Results.Ok(new { ok = true, db = "up" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/cv/analyze", async (HttpContext http, Kernel sk, TokenRateLimiter limiter, IFormFile file, CancellationToken ct) =>
{
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "arquivo vazio" });

    string raw;
    var nameLower = file.FileName?.ToLowerInvariant() ?? "";
    var ctype = file.ContentType?.ToLowerInvariant() ?? "";
    try
    {
        if (ctype.Contains("pdf") || nameLower.EndsWith(".pdf"))
        {
            using var s = file.OpenReadStream();
            raw = ExtractToPdfText.ExtractPdfText(s) ?? "";
        }
        else if (ctype.Contains("wordprocessingml") || nameLower.EndsWith(".docx"))
        {
            using var s = file.OpenReadStream();
            raw = ExtractDocxText(s);
        }
        else
        {
            using var r = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            raw = await r.ReadToEndAsync(ct);
        }
    }
    catch
    {
        return Results.BadRequest(new { error = "Falha ao abrir/ler o arquivo. Envie PDF texto, DOCX ou TXT." });
    }

    raw = raw.Replace("\r", "\n");
    raw = CleanLinkedInArtifacts(raw);
    raw = RemoveDuplicateHeaderFooters(raw);
    raw = NormalizePortugueseDates(raw);
    raw = TextSanitizer.Clean(raw);
    if (string.IsNullOrWhiteSpace(raw) || raw.Length < 80)
        return Results.BadRequest(new { error = "Não consegui extrair texto do arquivo. Envie PDF 'texto' (não imagem) ou .docx/.txt." });

    var nameHint = TryExtractName(raw);
    var contactHint = TryExtractContact(raw);
    var langHint = TryExtractLanguagesFlex(raw);

    var extractor = sk.GetRequiredService<IChatCompletionService>("extract");
    const int mapMaxOutput = 1500; // Aumentado para processar texto completo

    var mapSystem = """
Responda APENAS JSON:
{ "facts":[
  {"type":"person","fullName":"..."},
  {"type":"contact","email":"...","phone":"..."},
  {"type":"link","kind":"github|linkedin|portfolio|other","url":"..."},
  {"type":"exp","role":"...","company":"...","start":"YYYY-MM","end":"YYYY-MM|present","stack":[".NET","C#","SQL Server","Azure","React","Docker"]},
  {"type":"edu","title":"...","institution":"...","yearEnd":2025},
  {"type":"lang","name":"EN","level":"B2"},
  {"type":"skill","name":"C#","level":"intermediate"},
  {"type":"cert","code":"AZ-900"}
]}
REGRAS:
- Somente JSON válido. Se nada: {"facts":[]}.
- Datas PT/EN → ISO: "Maio de 2024"→"2024-05"; "até o momento"/"present"→"present".
- Normalize tecnologia (ex.: ".NET", "SQL Server", "Azure", "RabbitMQ", "Angular", "React").
- Ignore cabeçalhos/rodapés repetidos e “Page N of M”.
""";

    var factsAll = new List<JsonElement>();
    string? dbgIn = null, dbgOut = null;
    bool debugMap = string.Equals(http.Request.Query["debug"], "map", StringComparison.OrdinalIgnoreCase);

    // Processar todo o texto de uma vez
    var userMsg = "CV completo:\n" + raw;

    var need = TokenEstimator.EstimateTokens(mapSystem) + TokenEstimator.EstimateTokens(userMsg) + mapMaxOutput;
    await limiter.AwaitCapacityAsync(need, ct);

    var h = new ChatHistory();
    h.AddSystemMessage(mapSystem);
    h.AddUserMessage(userMsg);

    ChatMessageContent mapResult;
    try
    {
        mapResult = await extractor.GetChatMessageContentAsync(
            h,
            new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.None(), MaxTokens = mapMaxOutput },
            sk, ct);
    }
    catch (Exception ex) { 
        return Results.BadRequest(new { error = $"Erro ao processar CV: {ex.Message}" });
    }

    var json = JsonUtils.SafeTrimJson(mapResult.Content ?? "{}");
    if (debugMap) { dbgIn = raw.Length > 1000 ? raw[..1000] + "..." : raw; dbgOut = json; }

    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            foreach (var f in facts.EnumerateArray()) factsAll.Add(f.Clone());
    }
    catch (Exception ex) {
        return Results.BadRequest(new { error = $"Erro ao processar resposta JSON: {ex.Message}" });
    }

    if (!string.IsNullOrWhiteSpace(nameHint) && !factsAll.Any(IsTypeFunc("person")))
        factsAll.Add(ToJsonElement(new { type = "person", fullName = nameHint }));
    if (!string.IsNullOrWhiteSpace(contactHint.email) || !string.IsNullOrWhiteSpace(contactHint.phone))
        factsAll.Add(ToJsonElement(new { type = "contact", email = contactHint.email, phone = contactHint.phone }));
    foreach (var l in contactHint.links)
        factsAll.Add(ToJsonElement(new { type = "link", kind = l.kind, url = l.url }));
    foreach (var (n, lvl) in langHint)
        factsAll.Add(ToJsonElement(new { type = "lang", name = n, level = lvl }));

    if (factsAll.Count == 0)
        return Results.BadRequest(new { error = "Não foi possível identificar informações no arquivo. Tente outro formato (.docx/.txt) ou PDF texto." });

    if (debugMap)
        return Results.Ok(new { sampleChunk = dbgIn ?? "(n/a)", sampleMapOutput = dbgOut ?? "{\"facts\":[]}" });

    factsAll = MergeAndDedupeFacts(factsAll);
    factsAll = LiftSkillsFromExp(factsAll);

    var synthesizer = sk.GetRequiredService<IChatCompletionService>("synth");
    const int reduceMaxOutput = 1700;
    var payload = JsonSerializer.Serialize(new { nameHint, facts = factsAll.Take(2000) });
    var reduceSystem = """
Gere APENAS um objeto JSON "CareerAnalysis":
{
  "profile": {
    "fullName": "string",
    "yearsExperience": 0,
    "education": ["string"],
    "languages": ["PT-BR","EN-B2"],
    "desiredArea": "frontend|backend|data|ux|pm|cloud|qa|devops|"
  },
  "skills": {
    "hard": [{"name":"C#","level":"beginner|intermediate|advanced"}],
    "soft": [{"name":"Comunicação","level":"intermediate","evidence":"..."}],
    "certsOrCourses": ["AZ-900","RabbitMQ and Messaging Concepts","..."]
  },
  "strengths": ["..."],
  "gaps": { "hard": ["..."], "soft": ["..."] },
  "targetRoles": [
    { "title":"Backend .NET - senioridade coerente com os anos totais","why":"...","mustHave":["..."],"niceToHave":["..."] }
  ]
}
Use SOMENTE os "facts". Calcule yearsExperience somando intervalos (sem dupla contagem). Apenas JSON.
""";

    var needReduce = TokenEstimator.EstimateTokens(reduceSystem) + TokenEstimator.EstimateTokens(payload) + reduceMaxOutput;
    await limiter.AwaitCapacityAsync(needReduce, ct);

    string finalJson;
    try
    {
        var rh = new ChatHistory();
        rh.AddSystemMessage(reduceSystem);
        rh.AddUserMessage(payload);

        var rr = await synthesizer.GetChatMessageContentAsync(
            rh,
            new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.None(), MaxTokens = reduceMaxOutput },
            sk, ct);
        finalJson = JsonUtils.SafeTrimJson(rr.Content ?? "{}");
    }
    catch { finalJson = "{}"; }

    if (string.IsNullOrWhiteSpace(finalJson) || finalJson == "{}")
        finalJson = BuildCareerAnalysisFallback(factsAll);

    try
    {
        var yearsCalc = ComputeYearsFromFacts(factsAll);

        var node = JsonNode.Parse(finalJson) as JsonObject ?? new JsonObject();
        var profile = node["profile"] as JsonObject;
        if (profile is null)
        {
            profile = new JsonObject
            {
                ["fullName"] = "",
                ["yearsExperience"] = 0,
                ["education"] = new JsonArray(),
                ["languages"] = new JsonArray(),
                ["desiredArea"] = "backend"
            };
            node["profile"] = profile;
        }

        if (!string.IsNullOrWhiteSpace(nameHint))
        {
            var curr = profile["fullName"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(curr)) profile["fullName"] = nameHint;
        }

        var modelYears = profile["yearsExperience"]?.GetValue<double?>() ?? null;
        if (yearsCalc > 0 && (modelYears is null || Math.Abs(yearsCalc - modelYears.Value) > 0.4))
            profile["yearsExperience"] = Math.Round(yearsCalc, 1);

        var langSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile["languages"] is JsonArray existing)
        {
            foreach (var x in existing)
                if (x is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    langSet.Add(s);
        }
        foreach (var (n, l) in langHint)
            langSet.Add($"{n}-{l}".Replace("--", "-"));

        if (langSet.Count > 0)
            profile["languages"] = new JsonArray(langSet.Select(s => (JsonNode)s).ToArray());

        var yearsForLevel = yearsCalc > 0 ? yearsCalc : (profile["yearsExperience"]?.GetValue<double?>() ?? 0);
        UpgradeSeniority(node, yearsForLevel);

        finalJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
    catch
    {
    }

    return Results.Content(finalJson, "application/json");

    static string ExtractDocxText(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        try
        {
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return "";
            var sb = new StringBuilder(64 * 1024);
            foreach (var p in body.Elements<Paragraph>())
            {
                var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string CleanLinkedInArtifacts(string t)
    {
        var lines = t.Split('\n').Select(l => l.TrimEnd()).ToList();
        lines.RemoveAll(l => Regex.IsMatch(l, @"^Page\s+\d+\s+of\s+\d+$", RegexOptions.IgnoreCase));
        lines = lines.Where(l =>
               !l.Equals("Contato", StringComparison.OrdinalIgnoreCase)
            && !l.Equals("Languages", StringComparison.OrdinalIgnoreCase)
            && !l.Equals("Certifications", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("www.linkedin.com/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join('\n', lines);
    }

    static string RemoveDuplicateHeaderFooters(string t)
    {
        var lines = t.Split('\n').Select(l => l.Trim()).ToArray();
        var counts = lines.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var filtered = lines.Where(l => !(l.Length <= 40 && counts.TryGetValue(l, out var c) && c >= 3)).ToArray();
        return string.Join('\n', filtered);
    }

    static string NormalizePortugueseDates(string t)
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["janeiro"] = "01",
            ["fevereiro"] = "02",
            ["março"] = "03",
            ["marco"] = "03",
            ["abril"] = "04",
            ["maio"] = "05",
            ["junho"] = "06",
            ["julho"] = "07",
            ["agosto"] = "08",
            ["setembro"] = "09",
            ["outubro"] = "10",
            ["novembro"] = "11",
            ["dezembro"] = "12"
        };
        string ReplaceMonth(Match m)
        {
            var mes = m.Groups["mes"].Value;
            var ano = m.Groups["ano"].Value;
            var mm = mapa.TryGetValue(mes, out var v) ? v : "01";
            return $"{ano}-{mm}";
        }
        t = Regex.Replace(t, @"(?<mes>janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(?<ano>\d{4})",
                          new MatchEvaluator(ReplaceMonth), RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"até\s+o\s+momento", "present", RegexOptions.IgnoreCase);
        return t;
    }

    static (string email, string phone, List<(string kind, string url)> links) TryExtractContact(string text)
    {
        var email = Regex.Match(text, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase).Value;
        var phone = Regex.Match(text, @"(\+?\d{2}\s?)?(\(?\d{2}\)?\s?)?\d{4,5}[-\s]?\d{4}").Value;
        var links = new List<(string, string)>();
        foreach (Match m in Regex.Matches(text, @"https?://\S+"))
        {
            var url = m.Value.TrimEnd('.', ',', ')');
            var urlLower = url.ToLowerInvariant();
            var kind = urlLower.Contains("linkedin") ? "linkedin"
                     : urlLower.Contains("github") ? "github"
                     : (urlLower.Contains("portfolio") || urlLower.Contains("leandrokuranaga.github.io")) ? "portfolio"
                     : "other";
            links.Add((kind, url));
        }
        return (email, phone, links);
    }

    static string TryExtractName(string text)
    {
        var top = text.Split('\n').Take(14).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var emailIdx = Array.FindIndex(top, s => s.Contains("@"));
        if (emailIdx > 0) return top[emailIdx - 1].Length <= 80 ? top[emailIdx - 1] : "";
        var reFull = new Regex(@"^[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇà-ú'\-]+(\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇà-ú'\-]+){1,6}$");
        foreach (var s in top) if (reFull.IsMatch(s)) return s;
        return "";
    }

    static List<(string name, string level)> TryExtractLanguagesFlex(string text)
    {
        var res = new List<(string, string)>();
        var t = text.ToLowerInvariant();

        if (t.Contains("portugu")) res.Add(("PT-BR", "native"));
        if (t.Contains("english") || t.Contains("inglês"))
        {
            var level = t.Contains("full professional") || t.Contains("c1") ? "C1"
                      : t.Contains("professional") || t.Contains("b2") ? "B2"
                      : t.Contains("intermediate") || t.Contains("b1") ? "B1" : "B2";
            res.Add(("EN", level));
        }
        if (t.Contains("日本語") || t.Contains("japon")) res.Add(("JA", "A2"));
        return res;
    }

    static JsonElement ToJsonElement(object o) => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(o));

    static Func<JsonElement, bool> IsTypeFunc(string t) =>
        e => e.TryGetProperty("type", out var te) && string.Equals(te.GetString(), t, StringComparison.OrdinalIgnoreCase);

    static List<JsonElement> MergeAndDedupeFacts(List<JsonElement> facts)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exps = new List<(string role, string company, string start, string end, HashSet<string> stack)>();
        var others = new List<JsonElement>();

        foreach (var f in facts)
        {
            if (!f.TryGetProperty("type", out var te)) continue;
            var type = te.GetString()?.ToLowerInvariant();
            switch (type)
            {
                case "link":
                    var url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (url.Length > 0) links.Add(url);
                    break;
                case "lang":
                    var ln = (f.TryGetProperty("name", out var n) ? n.GetString() : null) ?? "";
                    var lv = (f.TryGetProperty("level", out var l) ? l.GetString() : null) ?? "";
                    if (ln.Length > 0) langs.Add($"{ln}-{lv}".Trim('-'));
                    break;
                case "skill":
                    var sn = (f.TryGetProperty("name", out var snv) ? snv.GetString() : null) ?? "";
                    var sl = (f.TryGetProperty("level", out var slv) ? slv.GetString() : null) ?? "intermediate";
                    if (sn.Length > 0)
                    {
                        if (!skills.TryGetValue(sn, out var cur) || Rank(sl) > Rank(cur)) skills[sn] = sl;
                    }
                    break;
                case "exp":
                    string g(string key) => f.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";
                    var role = g("role"); var comp = g("company");
                    var st = g("start"); var en = g("end");
                    var stks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (f.TryGetProperty("stack", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var it in arr.EnumerateArray())
                            if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                                stks.Add(NormTech(it.GetString()!));
                    var merged = false;
                    for (int i = 0; i < exps.Count; i++)
                    {
                        var e = exps[i];
                        if (string.Equals(e.role, role, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(e.company, comp, StringComparison.OrdinalIgnoreCase)
                         && Overlap(e.start, e.end, st, en))
                        {
                            e.start = MinDate(e.start, st);
                            e.end = MaxDate(e.end, en);
                            foreach (var sname in stks) e.stack.Add(sname);
                            exps[i] = e;
                            merged = true; break;
                        }
                    }
                    if (!merged) exps.Add((role, comp, st, en, stks));
                    break;
                default:
                    others.Add(f);
                    break;
            }
        }

        var res = new List<JsonElement>();
        foreach (var e in exps)
            res.Add(ToJsonElement(new { type = "exp", role = e.role, company = e.company, start = e.start, end = e.end, stack = e.stack.ToArray() }));
        foreach (var kv in skills)
            res.Add(ToJsonElement(new { type = "skill", name = kv.Key, level = kv.Value }));
        foreach (var l in langs)
        {
            var parts = l.Split('-', 2);
            res.Add(ToJsonElement(new { type = "lang", name = parts[0], level = parts.Length > 1 ? parts[1] : "" }));
        }
        foreach (var u in links)
            res.Add(ToJsonElement(new { type = "link", kind = GuessKind(u), url = u }));

        res.AddRange(others);
        return res;

        static int Rank(string lvl) => lvl?.ToLowerInvariant() switch
        {
            "beginner" => 1,
            "intermediate" => 2,
            "intermediate/advanced" => 3,
            "advanced" => 4,
            _ => 2
        };
        static string NormTech(string s) => s.Replace(" .NET", ".NET", StringComparison.OrdinalIgnoreCase).Trim();
        static string GuessKind(string url)
        {
            var u = url.ToLowerInvariant();
            if (u.Contains("linkedin")) return "linkedin";
            if (u.Contains("github")) return "github";
            if (u.Contains("portfolio") || u.Contains("leandrokuranaga.github.io")) return "portfolio";
            return "other";
        }
        static bool Overlap(string aS, string aE, string bS, string bE)
        {
            var (as_, ae_) = (Parse(aS, true), Parse(aE, false));
            var (bs_, be_) = (Parse(bS, true), Parse(bE, false));
            if (as_ == null || bs_ == null) return false;
            return (ae_ ?? DateTime.MaxValue) >= bs_ && (be_ ?? DateTime.MaxValue) >= as_;
            static DateTime? Parse(string s, bool start)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (s.Equals("present", StringComparison.OrdinalIgnoreCase)) return start ? DateTime.UtcNow : DateTime.MaxValue;
                if (s.Length == 7 && int.TryParse(s[..4], out var y) && int.TryParse(s.Substring(5, 2), out var m))
                    return new DateTime(y, m, 1);
                if (s.Length == 4 && int.TryParse(s, out var yy)) return new DateTime(yy, 1, 1);
                return null;
            }
        }
        static string MinDate(string a, string b) => Pick(a, b, true);
        static string MaxDate(string a, string b) => Pick(a, b, false);
        static string Pick(string a, string b, bool min)
        {
            DateTime? A = a.Equals("present", StringComparison.OrdinalIgnoreCase) ? DateTime.MaxValue : Parse(a);
            DateTime? B = b.Equals("present", StringComparison.OrdinalIgnoreCase) ? DateTime.MaxValue : Parse(b);
            if (A == null) return b; if (B == null) return a;
            var chosen = min ? (A <= B ? a : b) : (A >= B ? a : b);
            return chosen;
            static DateTime? Parse(string s)
            {
                if (s?.Length == 7 && int.TryParse(s[..4], out var y) && int.TryParse(s.Substring(5, 2), out var m)) return new DateTime(y, m, 1);
                if (s?.Length == 4 && int.TryParse(s, out var yy)) return new DateTime(yy, 1, 1);
                if (string.Equals(s, "present", StringComparison.OrdinalIgnoreCase)) return DateTime.MaxValue;
                return null;
            }
        }
    }

    static List<JsonElement> LiftSkillsFromExp(List<JsonElement> facts)
    {
        var outList = new List<JsonElement>(facts);
        var haveSkill = new HashSet<string>(
            facts.Where(IsTypeFunc("skill")).Select(f => f.GetProperty("name").GetString() ?? ""),
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in facts.Where(IsTypeFunc("exp")))
        {
            if (!f.TryGetProperty("stack", out var st) || st.ValueKind != JsonValueKind.Array) continue;
            foreach (var s in st.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.String) continue;
                var name = s.GetString() ?? "";
                if (name.Length == 0 || haveSkill.Contains(name)) continue;
                outList.Add(ToJsonElement(new { type = "skill", name = name, level = "intermediate" }));
                haveSkill.Add(name);
            }
        }
        return outList;
    }

    static double ComputeYearsFromFacts(IReadOnlyList<JsonElement> facts)
    {
        var spans = new List<(DateTime s, DateTime e)>();
        foreach (var f in facts.Where(IsTypeFunc("exp")))
        {
            DateTime? ParseYm(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (s.Equals("present", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow;
                if (s.Length == 4 && int.TryParse(s, out var y)) return new DateTime(y, 1, 1);
                if (s.Length >= 7 && int.TryParse(s[..4], out var yy) && int.TryParse(s.Substring(5, 2), out var mm) && mm is >= 1 and <= 12)
                    return new DateTime(yy, mm, 1);
                return null;
            }

            var sOk = f.TryGetProperty("start", out var se);
            var eOk = f.TryGetProperty("end", out var ee);
            DateTime? s = sOk ? ParseYm(se.GetString()) : null;
            DateTime? e = eOk ? ParseYm(ee.GetString()) : null;
            if (s.HasValue)
            {
                var end = e ?? DateTime.UtcNow;
                if (end > s.Value) spans.Add((s.Value, end));
            }
        }
        if (spans.Count == 0) return 0;
        spans = spans.OrderBy(x => x.s).ToList();
        var merged = new List<(DateTime s, DateTime e)>();
        var cur = spans[0];
        for (int i = 1; i < spans.Count; i++)
        {
            var nx = spans[i];
            if (nx.s <= cur.e)
            {
                if (nx.e > cur.e) cur.e = nx.e;
            }
            else
            {
                merged.Add(cur);
                cur = nx;
            }
        }
        merged.Add(cur);
        var totalDays = merged.Sum(x => (x.e - x.s).TotalDays);
        return Math.Round(totalDays / 365.25, 1);
    }

    static void UpgradeSeniority(JsonObject root, double years)
    {
        string level = years >= 6 ? "Sênior" : years >= 3 ? "Pleno" : "Júnior";
        var desired = root["profile"]?["desiredArea"]?.GetValue<string>()?.ToLowerInvariant() ?? "backend";
        string areaTitle = desired switch
        {
            "frontend" => "Frontend",
            "data" => "Data",
            "cloud" => "Cloud",
            "qa" => "QA",
            "devops" => "DevOps",
            _ => "Backend .NET"
        };
        if (root["targetRoles"] is not JsonArray arr)
        {
            arr = new JsonArray();
            root["targetRoles"] = arr;
        }
        if (arr.Count == 0)
        {
            arr.Add(new JsonObject
            {
                ["title"] = $"{level} {areaTitle}",
                ["why"] = "Atribuído conforme anos totais de experiência.",
                ["mustHave"] = new JsonArray("C#/.NET", "SQL", "Git"),
                ["niceToHave"] = new JsonArray("Cloud", "Docker")
            });
            return;
        }
        foreach (var item in arr.OfType<JsonObject>())
        {
            var title = item["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title)) { item["title"] = $"{level} {areaTitle}"; continue; }
            title = Regex.Replace(title, @"\b(Jr\.?|Junior|Pleno|Sênior|Senior)\b", "", RegexOptions.IgnoreCase).Trim();
            if (title.IndexOf(areaTitle, StringComparison.OrdinalIgnoreCase) < 0) title = $"{areaTitle} {title}".Trim();
            item["title"] = $"{level} {title}".Replace("  ", " ").Trim();
        }
    }

}).DisableAntiforgery();


app.MapPost("/api/roadmap/from-cv", async (Kernel sk, TokenRateLimiter limiter, PlanRequest req, CancellationToken ct) =>
{
    if (req.Analysis.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "analysis deve ser um objeto JSON (não string)." });
    if (req.HoursPerWeek <= 0 || req.HorizonMonths <= 0)
        return Results.BadRequest(new { error = "hoursPerWeek e horizonMonths devem ser > 0." });

    var chat = sk.GetRequiredService<IChatCompletionService>("synth");

    var sys1 = """
Gere APENAS um JSON "CareerPlan" (um único objeto). Não use crases, não use ```json.
Formato alvo (flexível, mas mantenha as chaves):
{
  "summary": { "readinessScore": 0, "timeBudgetHoursPerWeek": 0, "horizonMonths": 6, "headline": "..." },
  "path": [
    {
      "phase": "Mês X-Y | Título",
      "outcomes": ["..."],
      "hardSkills": ["..."],
      "softSkills": [{"name":"Comunicação","exercise":"atividade prática"}],
      "diyProjects": [{"title":"...","brief":"...","acceptanceCriteria":["..."]}],
      "resources": [{"title":"...","url":"...","type":"course|doc|tool","free":true}],
      "milestones": [{"title":"...","dueDays":14,"kpis":{"testsCoverage":0.6}}]
    }
  ],
  "interviewPlan": { "schedule": ["..."], "questionBankTags": ["..."], "homeworks": ["..."] },
  "networkingPlan": { "weeklyRoutines": ["..."], "targets": ["..."] },
  "kpis": { "portfolioProjects": 3, "passedMockInterviews": 3, "softSkillPracticesPerWeek": 2 },
  "nextActions": [{"title":"...","effortHours":2,"dueDate":"2025-09-05T23:59:00Z"}]
}
Regras:
- Use SOMENTE os dados de "analysis". Não invente experiências.
- Adapte ao foco (focusArea) e ao tempo semanal (hoursPerWeek).
- Cubra o horizonte (horizonMonths) com fases realistas.
- Se faltar info, deixe "" ou [] — mas mantenha TODAS as chaves.
""";

    var payloadNode = new JsonObject
    {
        ["analysis"] = JsonNode.Parse(req.Analysis.GetRawText()),
        ["hoursPerWeek"] = req.HoursPerWeek,
        ["horizonMonths"] = req.HorizonMonths,
        ["focusArea"] = req.FocusArea ?? ""
    };
    var payload = payloadNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    const int maxOutput1 = 1500;
    await limiter.AwaitCapacityAsync(TokenEstimator.EstimateTokens(sys1) + TokenEstimator.EstimateTokens(payload) + maxOutput1, ct);

    var h1 = new ChatHistory();
    h1.AddSystemMessage(sys1);
    h1.AddUserMessage(payload);

    string? json;
    try
    {
        var r1 = await chat.GetChatMessageContentAsync(
            h1,
            new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.None(), MaxTokens = maxOutput1 },
            sk,
            ct
        );
        json = JsonUtils.SafeTrimJson(r1.Content ?? "{}");
    }
    catch { json = "{}"; }

    if (string.IsNullOrWhiteSpace(json) || json == "{}")
        json = BuildPlanFallback(req.Analysis, req.HoursPerWeek, req.HorizonMonths, req.FocusArea ?? "");

    return Results.Content(json, "application/json");
});

app.MapPost("/api/coach", async (
    CareerMentor.Api.Infra.Context db,
    Kernel sk,
    TokenRateLimiter limiter,
    CoachStreamRequest req,
    CancellationToken ct) =>
{
    var chat = sk.GetRequiredService<IChatCompletionService>("synth");

    string preamble = BuildCoachPreamble(req, await LoadRecentGoalsAsync(db, req.GoalsLimit ?? 5, ct));

    var sys = $@"
Você é um COACH DE CARREIRA para iniciantes.
Use ESTRITAMENTE o contexto abaixo como verdade (não invente):

=== CONTEXTO ===
{preamble}
=== FIM CONTEXTO ===

Responda APENAS um objeto JSON com as chaves:
{{
  ""message"": ""texto curto e direto"",
  ""next_actions"": [{{""title"":""..."",""effortHours"":1,""dueDate"":""...""}}],
  ""tips"": [""...""] ,
  ""progressHooks"": [{{""taskKey"":""phase1.api_v1"",""action"":""mark_next""}}]
}}
Sem crases e sem ```json.
";

    var history = new ChatHistory();
    history.AddSystemMessage(sys);
    foreach (var m in req.Messages ?? new())
    {
        var role = m.Role?.ToLowerInvariant();
        if (role == "system") history.AddSystemMessage(m.Content);
        else if (role == "assistant") history.AddAssistantMessage(m.Content);
        else history.AddUserMessage(m.Content);
    }

    var inCount = new StringBuilder(sys);
    foreach (var m in req.Messages ?? new()) inCount.Append('\n').Append(m.Role).Append(": ").Append(m.Content);
    const int maxOutput = 600;
    await limiter.AwaitCapacityAsync(TokenEstimator.EstimateTokens(inCount.ToString()) + maxOutput, ct);

    string responseJson;
    try
    {
        var result = await chat.GetChatMessageContentAsync(
            history,
            new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.None(), MaxTokens = maxOutput },
            sk,
            ct);
        responseJson = JsonUtils.SafeTrimJson(result.Content ?? "{}");
    }
    catch
    {
        responseJson = @"{""message"":""Beleza, vamos começar."",""next_actions"":[{""title"":""Definir escopo de um microserviço .NET (CRUD)"",""effortHours"":3,""dueDate"":""""},{""title"":""Criar API .NET 8 + EF Core (MVP + README)"",""effortHours"":4,""dueDate"":""""},{""title"":""Escrever 3 testes xUnit e publicar no GitHub"",""effortHours"":3,""dueDate"":""""}],""tips"":[""Use minimal APIs e um banco local (SQLite/PG).""],""progressHooks"":[]}";
    }

    // Salvar no banco
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var goal = new CareerMentor.Api.Domain.Goal
        {
            Title = $"Coach sprint - {DateTime.UtcNow:yyyy-MM-dd}",
            Horizon = "14d",
            Metric = "Completar next_actions do coach"
        };

        if (root.TryGetProperty("next_actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var na in arr.EnumerateArray())
            {
                var title = na.TryGetProperty("title", out var te) ? te.GetString() : null;
                var hours = na.TryGetProperty("effortHours", out var he) && he.TryGetInt32(out var hv) ? hv : 1;
                DateTime? due = null;
                if (na.TryGetProperty("dueDate", out var de) && de.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(de.GetString(), out var d))
                    due = d.ToUniversalTime();

                if (!string.IsNullOrWhiteSpace(title))
                    goal.NextActions.Add(new CareerMentor.Api.Domain.NextAction { Title = title!, EffortHours = hours, DueDate = due });
            }
        }

        if (goal.NextActions.Count > 0)
        {
            db.Goals.Add(goal);
            await db.SaveChangesAsync(ct);
        }
    }
    catch { /* best effort */ }

    return Results.Content(responseJson, "application/json");
});

app.MapPost("/api/chat", async (
    Kernel sk,
    TokenRateLimiter limiter,
    ChatRequest req,
    CancellationToken ct) =>
{
    if (req.Messages is null || req.Messages.Count == 0)
        return Results.BadRequest(new { error = "Messages não pode estar vazio." });

    var chat = sk.GetRequiredService<IChatCompletionService>("synth");

    var sys = """
Você é um assistente de carreira especializado em tecnologia, amigável e prático.

Diretrizes:
- Responda de forma concisa e objetiva (máximo 3 parágrafos)
- Foque em conselhos práticos e acionáveis
- Use linguagem acessível, sem jargões excessivos
- Para perguntas técnicas, dê exemplos concretos
- Para perguntas de carreira, seja realista sobre mercado atual
- Se não souber algo específico, seja honesto e sugira onde buscar

Áreas de expertise:
- Desenvolvimento de software (.NET, JavaScript, Python, etc.)
- Transição de carreira para tech
- Preparação para entrevistas técnicas
- Construção de portfólio
- Roadmaps de aprendizado
- Mercado de trabalho tech no Brasil
""";

    var history = new ChatHistory();
    history.AddSystemMessage(sys);
    
    foreach (var m in req.Messages)
    {
        var role = m.Role?.ToLowerInvariant();
        if (role == "system") history.AddSystemMessage(m.Content);
        else if (role == "assistant") history.AddAssistantMessage(m.Content);
        else history.AddUserMessage(m.Content);
    }

    var inCount = new StringBuilder(sys);
    foreach (var m in req.Messages) inCount.Append('\n').Append(m.Role).Append(": ").Append(m.Content);
    const int maxOutput = 400;
    await limiter.AwaitCapacityAsync(TokenEstimator.EstimateTokens(inCount.ToString()) + maxOutput, ct);

    try
    {
        var result = await chat.GetChatMessageContentAsync(
            history,
            new OpenAIPromptExecutionSettings { 
                FunctionChoiceBehavior = FunctionChoiceBehavior.None(), 
                MaxTokens = maxOutput,
                Temperature = 0.7f
            },
            sk,
            ct);

        var response = new { 
            message = result.Content?.Trim() ?? "Desculpe, não consegui processar sua mensagem." 
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Erro ao processar mensagem: {ex.Message}" });
    }
});


static async Task<List<CareerMentor.Api.Domain.Goal>> LoadRecentGoalsAsync(
    CareerMentor.Api.Infra.Context db, int take, CancellationToken ct)
{
    return await db.Goals
        .Include(g => g.NextActions)
        .OrderByDescending(g => g.CreatedAt)
        .Take(Math.Max(0, Math.Min(10, take)))
        .ToListAsync(ct);
}

static string BuildCoachPreamble(CoachStreamRequest req, List<CareerMentor.Api.Domain.Goal> recentGoals)
{
    var sb = new StringBuilder();

    if (req.Analysis.HasValue && req.Analysis.Value.ValueKind == JsonValueKind.Object)
    {
        sb.AppendLine("ANALYSIS:");
        sb.AppendLine(req.Analysis.Value.GetRawText());
        sb.AppendLine();
    }

    if (req.Plan.HasValue && req.Plan.Value.ValueKind == JsonValueKind.Object)
    {
        sb.AppendLine("CAREER PLAN:");
        sb.AppendLine(req.Plan.Value.GetRawText());
        sb.AppendLine();
    }

    if (req.Extras.HasValue && req.Extras.Value.ValueKind is JsonValueKind.Object)
    {
        sb.AppendLine("EXTRAS:");
        sb.AppendLine(req.Extras.Value.GetRawText());
        sb.AppendLine();
    }

    if (recentGoals.Count > 0)
    {
        var compact = recentGoals.Select(g => new
        {
            g.Title,
            g.Horizon,
            g.Metric,
            createdAt = g.CreatedAt.ToString("o"),
            next_actions = g.NextActions.Select(a => new { a.Title, a.EffortHours, dueDate = a.DueDate?.ToString("o") })
        });
        sb.AppendLine("RECENT_GOALS:");
        sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(compact));
        sb.AppendLine();
    }

    return sb.ToString();
}

app.MapGet("/api/goals", async (Context db, CancellationToken ct) =>
{
    var data = await db.Goals
        .Include(g => g.NextActions)
        .AsNoTracking()
        .OrderByDescending(g => g.CreatedAt)
        .ToListAsync(ct);
    return Results.Ok(data);
});

app.MapPost("/api/goals", async (Context db, CareerMentor.Api.Domain.Goal g, CancellationToken ct) =>
{
    db.Goals.Add(g);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/goals/{g.Id}", g);
});

app.Run();

string BuildPlanFallback(JsonElement analysis, int hoursPerWeek, int horizonMonths, string focusArea)
{
    string desired = "";
    double years = 0;
    var hardSkills = new List<string>();
    try
    {
        if (analysis.TryGetProperty("profile", out var prof))
        {
            desired = prof.TryGetProperty("desiredArea", out var d) ? d.GetString() ?? "" : "";
            if (prof.TryGetProperty("yearsExperience", out var y)
                && y.ValueKind == JsonValueKind.Number && y.TryGetDouble(out var yy))
                years = yy;
        }
        if (analysis.TryGetProperty("skills", out var sks)
            && sks.TryGetProperty("hard", out var h) && h.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in h.EnumerateArray())
                if (it.TryGetProperty("name", out var n))
                { var s = n.GetString(); if (!string.IsNullOrWhiteSpace(s)) hardSkills.Add(s!); }
        }
    }
    catch { }

    var focus = (string.IsNullOrWhiteSpace(focusArea) ? desired : focusArea).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(focus)) focus = "backend";

    int months = Math.Max(3, Math.Min(12, horizonMonths));
    int span = months <= 4 ? 1 : 2;
    var phases = new List<JsonObject>();

    var topics = focus switch
    {
        "frontend" => new[] { "HTML", "CSS", "JavaScript", "React", "Testing", "A11y", "Deploy" },
        "data" => new[] { "Python", "SQL", "Pandas", "EDA", "ETL", "Dashboards", "ML básico" },
        "cloud" => new[] { "Linux", "Containers", "Docker", "K8s básico", "CI/CD", "Cloud Provider", "Observability" },
        "qa" => new[] { "Testes Funcionais", "API Testing", "Automação", "CI", "Relatórios", "Perf/Load" },
        "devops" => new[] { "Git/GitFlow", "CI/CD", "IaC", "Docker/K8s", "Cloud Basics", "Observability", "Security basics" },
        _ => new[] { "C#", ".NET 8 APIs", "EF Core", "SQL/Índices", "Mensageria", "Cache/Redis", "Observability" }
    };

    int month = 1, idx = 0;
    while (month <= months)
    {
        var start = month;
        var end = Math.Min(month + span - 1, months);
        var slice = topics.Skip(idx).Take(2).ToArray();
        if (slice.Length == 0) slice = topics.Take(2).ToArray();
        idx += slice.Length;

        var hard = new JsonArray(slice.Select(s => (JsonNode)s).ToArray());
        var soft = new JsonArray(
            new JsonObject { ["name"] = "Comunicação", ["exercise"] = "1x/semana: explicar em 5min o que você construiu" },
            new JsonObject { ["name"] = "Gestão de tempo", ["exercise"] = "Planejar semana com timeboxing" }
        );
        var diy = new JsonArray(
            new JsonObject
            {
                ["title"] = focus == "backend" ? $"API .NET + {string.Join(" + ", slice)}" : $"Projeto {string.Join(" + ", slice)}",
                ["brief"] = "Defina um escopo pequeno de negócio e entregue valor semanal.",
                ["acceptanceCriteria"] = new JsonArray("rodando local", "README", "tests básicos")
            }
        );
        var resources = new JsonArray(
            new JsonObject { ["title"] = $"{slice[0]} — doc/curso", ["url"] = "", ["type"] = "doc", ["free"] = true },
            new JsonObject { ["title"] = $"{slice[^1]} — doc/curso", ["url"] = "", ["type"] = "doc", ["free"] = true }
        );
        var milestones = new JsonArray(
            new JsonObject { ["title"] = "MVP entregue", ["dueDays"] = 14, ["kpis"] = new JsonObject { ["testsCoverage"] = 0.3 } }
        );

        phases.Add(new JsonObject
        {
            ["phase"] = $"Mês {start}{(end > start ? $"-{end}" : "")} | {string.Join(" + ", slice)}",
            ["outcomes"] = new JsonArray("Aprender fundamentos", "Entregar um MVP"),
            ["hardSkills"] = hard,
            ["softSkills"] = soft,
            ["diyProjects"] = diy,
            ["resources"] = resources,
            ["milestones"] = milestones
        });

        month += span;
    }

    var plan = new JsonObject
    {
        ["summary"] = new JsonObject
        {
            ["readinessScore"] = Math.Min(100, (int)Math.Round(years * 12)),
            ["timeBudgetHoursPerWeek"] = hoursPerWeek,
            ["horizonMonths"] = months,
            ["headline"] = $"{focus} roadmap inicial"
        },
        ["path"] = new JsonArray(phases.Cast<JsonNode>().ToArray()),
        ["interviewPlan"] = new JsonObject
        {
            ["schedule"] = new JsonArray("1x/semana: simulados", "1x/semana: revisão técnica"),
            ["questionBankTags"] = new JsonArray(focus, "fundamentos"),
            ["homeworks"] = new JsonArray("Atualizar portfólio", "Escrever post técnico curto")
        },
        ["networkingPlan"] = new JsonObject
        {
            ["weeklyRoutines"] = new JsonArray("2 novas conexões no LinkedIn", "1 post técnico/semana"),
            ["targets"] = new JsonArray("Comunidades online da área")
        },
        ["kpis"] = new JsonObject
        {
            ["portfolioProjects"] = Math.Max(2, months / 2),
            ["passedMockInterviews"] = Math.Max(2, months / 3),
            ["softSkillPracticesPerWeek"] = 2
        },
        ["nextActions"] = new JsonArray(
            new JsonObject { ["title"] = "Definir tema do primeiro projeto", ["effortHours"] = Math.Max(2, hoursPerWeek / 3), ["dueDate"] = DateTime.UtcNow.AddDays(7).ToString("o") }
        )
    };

    return plan.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

string BuildCareerAnalysisFallback(IReadOnlyList<JsonElement> facts)
{
    var hard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var soft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var certs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var edu = new List<string>();
    var langs = new List<string>();
    var roles = new List<string>();
    var spans = new List<(DateTime? s, DateTime? e)>();

    foreach (var f in facts)
    {
        if (!f.TryGetProperty("type", out var typeEl)) continue;
        var type = typeEl.GetString()?.ToLowerInvariant();

        if (type == "exp")
        {
            if (f.TryGetProperty("role", out var roleEl))
            {
                var role = roleEl.GetString();
                if (!string.IsNullOrWhiteSpace(role)) roles.Add(role);
            }

            var s = ParseYmFromProp(f, "start");
            var e = ParseYmOrPresentFromProp(f, "end");
            spans.Add((s, e));

            if (f.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in st.EnumerateArray())
                {
                    var name = t.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    hard.TryGetValue(name!, out var c);
                    hard[name!] = c + 1;
                }
            }
        }
        else if (type == "edu")
        {
            var title = f.TryGetProperty("title", out var te) ? te.GetString() : null;
            var inst = f.TryGetProperty("institution", out var ie) ? ie.GetString() : null;
            var item = new[] { title, inst }.Where(s0 => !string.IsNullOrWhiteSpace(s0));
            var join = string.Join(" - ", item);
            if (!string.IsNullOrWhiteSpace(join)) edu.Add(join);
        }
        else if (type == "lang")
        {
            var n = f.TryGetProperty("name", out var ne) ? ne.GetString() : null;
            var l = f.TryGetProperty("level", out var le) ? le.GetString() : null;
            if (!string.IsNullOrWhiteSpace(n))
                langs.Add(!string.IsNullOrWhiteSpace(l) ? $"{n}-{l}" : n!);
        }
        else if (type == "skill")
        {
            var n = f.TryGetProperty("name", out var ne) ? ne.GetString() : null;
            if (!string.IsNullOrWhiteSpace(n))
            {
                hard.TryGetValue(n!, out var c);
                hard[n!] = c + 1;
            }
        }
        else if (type == "cert")
        {
            var code = f.TryGetProperty("code", out var ce) ? ce.GetString() : null;
            if (!string.IsNullOrWhiteSpace(code)) certs.Add(code!);
        }
    }

    double years = 0;
    foreach (var (s, e) in spans)
    {
        var end = e ?? DateTime.UtcNow;
        if (s.HasValue && end > s.Value)
            years += (end - s.Value).TotalDays / 365.25;
    }
    years = Math.Round(years, 1);

    var hardList = hard
        .OrderByDescending(kv => kv.Value)
        .Take(20)
        .Select(kv => new JsonObject
        {
            ["name"] = kv.Key,
            ["level"] = "intermediate",
            ["evidence"] = "extraído de experiências/stack"
        }).ToList();

    var obj = new JsonObject
    {
        ["profile"] = new JsonObject
        {
            ["fullName"] = "",
            ["yearsExperience"] = years,
            ["education"] = new JsonArray(edu.Select(e => (JsonNode)e).ToArray()),
            ["languages"] = new JsonArray(langs.Select(e => (JsonNode)e).ToArray()),
            ["desiredArea"] = InferDesiredArea(roles, hard.Keys)
        },
        ["skills"] = new JsonObject
        {
            ["hard"] = new JsonArray(hardList.Cast<JsonNode>().ToArray()),
            ["soft"] = new JsonArray(),
            ["certsOrCourses"] = new JsonArray(certs.Select(c => (JsonNode)c).ToArray())
        },
        ["strengths"] = new JsonArray(),
        ["gaps"] = new JsonObject
        {
            ["hard"] = new JsonArray(),
            ["soft"] = new JsonArray()
        },
        ["targetRoles"] = new JsonArray()
    };

    return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    DateTime? ParseYmFromProp(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) ? ParseYmFromString(v.GetString()) : null;

    DateTime? ParseYmOrPresentFromProp(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Equals("present", StringComparison.OrdinalIgnoreCase)) return null;
        return ParseYmFromString(s);
    }

    DateTime? ParseYmFromString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s!.Length == 4 && int.TryParse(s, out var year))
            return new DateTime(year, 1, 1);
        if (s.Length >= 7 &&
            int.TryParse(s[..4], out var y) &&
            int.TryParse(s.Substring(5, 2), out var m) &&
            m is >= 1 and <= 12)
            return new DateTime(y, m, 1);
        return null;
    }

    string InferDesiredArea(IEnumerable<string> roleList, IEnumerable<string> skills)
    {
        var hay = string.Join(" ", roleList.Concat(skills)).ToLowerInvariant();
        if (hay.Contains("devops")) return "devops";
        if (hay.Contains("backend") || hay.Contains("c#") || hay.Contains(".net") || hay.Contains("java") || hay.Contains("node"))
            return "backend";
        if (hay.Contains("frontend") || hay.Contains("react") || hay.Contains("vue") || hay.Contains("angular"))
            return "frontend";
        if (hay.Contains("data") || hay.Contains("sql") || hay.Contains("python"))
            return "data";
        if (hay.Contains("qa") || hay.Contains("test"))
            return "qa";
        if (hay.Contains("cloud") || hay.Contains("azure") || hay.Contains("aws") || hay.Contains("gcp"))
            return "cloud";
        return "";
    }
}
