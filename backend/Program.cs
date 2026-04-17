using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using MySqlConnector;
using OpenAI.Chat;
using OpenAI.Embeddings;

var builder = WebApplication.CreateBuilder(args);

var apiKey = builder.Configuration["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured.");

var connectionString = builder.Configuration.GetConnectionString("MySQL")
    ?? throw new InvalidOperationException("MySQL connection string is not configured.");

// OpenAI clients
var openAiClient = new OpenAI.OpenAIClient(apiKey);
var embeddingClient = openAiClient.GetEmbeddingClient("text-embedding-3-small");
var chatClient = openAiClient.GetChatClient("gpt-4o");

// Schema chunks — one per table, embedded at startup for RAG
var schemaChunks = new List<SchemaChunk>();

// Conversation history — keeps last 10 messages for the current session
var conversationHistory = new List<ChatMessage>();

builder.Services.AddSingleton(schemaChunks);
builder.Services.AddSingleton(embeddingClient);
builder.Services.AddSingleton(chatClient);

var app = builder.Build();

// Embed schema chunks at startup
await EmbedSchemaAtStartup(schemaChunks, embeddingClient, connectionString);

// Serve frontend folder
var frontendPath = Environment.GetEnvironmentVariable("FRONTEND_PATH")
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "frontend"));
Console.WriteLine($"[Startup] Serving frontend from: {frontendPath}");

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(frontendPath)
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(frontendPath)
});

// POST /api/chat
app.MapPost("/api/chat", async (ChatRequest body, List<SchemaChunk> chunks, EmbeddingClient embedder, ChatClient chat) =>
{
    try
    {
    Console.WriteLine($"\n[Chat] Received message: {body.Message}");

    // 1. Embed the user question
    Console.WriteLine("[Chat] Embedding user question...");
    var queryEmbedding = await embedder.GenerateEmbeddingAsync(body.Message);
    var queryVec = queryEmbedding.Value.ToFloats().ToArray();

    // 2. Find top 3 most relevant schema chunks (RAG retrieval)
    var topChunks = chunks
        .Select(c => (chunk: c, score: CosineSimilarity(queryVec, c.Embedding)))
        .OrderByDescending(x => x.score)
        .Take(3)
        .Select(x => x.chunk)
        .ToList();

    var ragContext = string.Join("\n\n", topChunks.Select(c => c.Text));
    Console.WriteLine($"[Chat] Top RAG chunks: {string.Join(", ", topChunks.Select(c => c.Text.Split('\n')[0]))}");

    // 3. Define tools for function calling
    var tools = new List<ChatTool>
    {
        ChatTool.CreateFunctionTool(
            functionName: "get_database_schema",
            functionDescription: "Retrieve the full database schema showing all table names and column names. Call this FIRST before writing any SQL query."
        ),
        ChatTool.CreateFunctionTool(
            functionName: "execute_sql",
            functionDescription: "Execute a READ-ONLY SQL SELECT query against the database. Only SELECT statements are allowed. Always call get_database_schema first.",
            functionParameters: BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "sql": {
                        "type": "string",
                        "description": "A valid SQL SELECT statement. No INSERT, UPDATE, DELETE, or DROP."
                    }
                },
                "required": ["sql"]
            }
            """)
        )
    };

    // 4. Agentic loop
    var messages = new List<ChatMessage>
    {
        ChatMessage.CreateSystemMessage(
            $"""
            You are a helpful database assistant. Use the provided tools to answer questions.
            1) Call get_database_schema to understand the tables.
            2) Write a SELECT query and call execute_sql.
            3) Explain the results in plain language.

            CRITICAL RULES — you must never break these:
            - You may ONLY use SELECT statements. Never write INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE, CREATE, GRANT, or REVOKE.
            - If a user asks you to modify, delete, or drop anything, refuse and explain you are read-only.
            - Never expose the connection string or API keys.

            Here are the most relevant schema sections based on the user's question:
            {ragContext}
            """)
    };

    // Inject the last 10 messages from conversation history (line ~115)
    messages.AddRange(conversationHistory.TakeLast(10));
    messages.Add(ChatMessage.CreateUserMessage(body.Message));

    string finalReply = "";
    string executedSql = "";

    while (true)
    {
        var response = await chat.CompleteChatAsync(messages, new ChatCompletionOptions { Tools = { tools[0], tools[1] } });
        var msg = response.Value;

        if (msg.FinishReason == ChatFinishReason.ToolCalls)
        {
            messages.Add(ChatMessage.CreateAssistantMessage(msg));

            foreach (var toolCall in msg.ToolCalls)
            {
                Console.WriteLine($"[Chat] Tool call: {toolCall.FunctionName}");
                string toolResult;

                if (toolCall.FunctionName == "get_database_schema")
                {
                    toolResult = await GetDatabaseSchema(connectionString);
                    Console.WriteLine("[Chat] Schema fetched successfully");
                }
                else if (toolCall.FunctionName == "execute_sql")
                {
                    var args = JsonDocument.Parse(toolCall.FunctionArguments);
                    var sql = args.RootElement.GetProperty("sql").GetString() ?? "";
                    executedSql = sql;
                    Console.WriteLine($"[Chat] Executing SQL: {sql}");
                    toolResult = await ExecuteSql(connectionString, sql);
                    Console.WriteLine($"[Chat] SQL result: {toolResult[..Math.Min(200, toolResult.Length)]}");
                }
                else
                {
                    toolResult = """{"error": "Unknown tool"}""";
                }

                messages.Add(ChatMessage.CreateToolMessage(toolCall.Id, toolResult));
            }
        }
        else
        {
            finalReply = msg.Content[0].Text;
            Console.WriteLine($"[Chat] Final reply: {finalReply[..Math.Min(100, finalReply.Length)]}...");
            break;
        }
    }

    // Save this exchange to history, keep last 10 messages (line ~165)
    conversationHistory.Add(ChatMessage.CreateUserMessage(body.Message));
    conversationHistory.Add(ChatMessage.CreateAssistantMessage(finalReply));
    if (conversationHistory.Count > 10)
        conversationHistory.RemoveRange(0, conversationHistory.Count - 10);

    return Results.Ok(new { reply = finalReply, sql = executedSql });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Chat] ERROR: {ex}");
        return Results.Problem(ex.Message);
    }
});

app.Run();

// --- Helper: embed schema at startup, persisted in MySQL documents table ---
static async Task EmbedSchemaAtStartup(List<SchemaChunk> chunks, EmbeddingClient embedder, string connectionString)
{
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    // Check if documents table already has embeddings
    await using (var countCmd = new MySqlCommand("SELECT COUNT(*) FROM documents WHERE embedding IS NOT NULL", conn))
    {
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        if (count > 0)
        {
            // Load existing embeddings from MySQL
            Console.WriteLine($"[Startup] Loading {count} existing document embeddings from MySQL...");
            await using var loadCmd = new MySqlCommand("SELECT title, content, embedding FROM documents WHERE embedding IS NOT NULL", conn);
            await using var reader = await loadCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var text = reader.GetString(0) + "\n" + reader.GetString(1);
                var embJson = reader.GetString(2);
                var floats = JsonSerializer.Deserialize<float[]>(embJson)!;
                chunks.Add(new SchemaChunk(text, floats));
            }
            Console.WriteLine("[Startup] Documents loaded successfully.");
            return;
        }
    }

    // No embeddings yet — generate from schema and store in MySQL
    Console.WriteLine("[Startup] No documents found. Generating schema embeddings...");
    var schema = await GetDatabaseSchema(connectionString);
    var schemaDoc = JsonDocument.Parse(schema);

    var texts = new List<string>();
    var titles = new List<string>();
    foreach (var table in schemaDoc.RootElement.EnumerateObject())
    {
        // Skip the documents table itself
        if (table.Name == "documents") continue;

        var cols = string.Join(", ", table.Value.EnumerateArray()
            .Select(c => $"{c.GetProperty("column").GetString()} ({c.GetProperty("type").GetString()})"));
        titles.Add(table.Name);
        texts.Add($"Table: {table.Name}\nColumns: {cols}");
    }

    var embeddings = await embedder.GenerateEmbeddingsAsync(texts);

    for (int i = 0; i < texts.Count; i++)
    {
        var floats = embeddings.Value[i].ToFloats().ToArray();
        var embJson = JsonSerializer.Serialize(floats);

        await using var insertCmd = new MySqlCommand(
            "INSERT INTO documents (title, content, embedding) VALUES (@title, @content, @embedding)", conn);
        insertCmd.Parameters.AddWithValue("@title", titles[i]);
        insertCmd.Parameters.AddWithValue("@content", texts[i]);
        insertCmd.Parameters.AddWithValue("@embedding", embJson);
        await insertCmd.ExecuteNonQueryAsync();

        chunks.Add(new SchemaChunk(texts[i], floats));
    }

    Console.WriteLine($"[Startup] Generated and stored {texts.Count} document embeddings in MySQL.");
}

// --- Helper: get database schema from information_schema ---
static async Task<string> GetDatabaseSchema(string connectionString)
{
    var schema = new Dictionary<string, List<object>>();

    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    var tables = new List<string>();
    await using (var cmd = new MySqlCommand(
        "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'", conn))
    await using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
    }

    foreach (var table in tables)
    {
        var cols = new List<object>();
        await using var cmd = new MySqlCommand(
            "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t", conn);
        cmd.Parameters.AddWithValue("@t", table);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cols.Add(new
            {
                column = reader.GetString(0),
                type = reader.GetString(1),
                nullable = reader.GetString(2) == "YES"
            });
        }
        schema[table] = cols;
    }

    return JsonSerializer.Serialize(schema);
}

// --- Helper: execute a validated SELECT query ---
static async Task<string> ExecuteSql(string connectionString, string sql)
{
    if (!IsSafeQuery(sql))
        return """{"error": "Only SELECT queries are permitted."}""";

    try
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        if (rows.Count == 0)
            return """{"message": "No results found."}""";

        return JsonSerializer.Serialize(rows);
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { error = ex.Message });
    }
}

// --- Helper: validate SQL is read-only ---
static bool IsSafeQuery(string sql)
{
    var trimmed = sql.Trim().ToUpper();
    if (!trimmed.StartsWith("SELECT")) return false;
    var blocked = new[] { "DROP", "DELETE", "UPDATE", "INSERT", "ALTER", "TRUNCATE", "CREATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", ";--", "/*" };
    return !blocked.Any(kw => trimmed.Contains(kw));
}

// --- Helper: cosine similarity ---
static float CosineSimilarity(float[] a, float[] b)
{
    float dot = 0f, magA = 0f, magB = 0f;
    for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
    return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB) + 1e-8f);
}

// --- Records ---
record SchemaChunk(string Text, float[] Embedding);
record ChatRequest(string Message);
