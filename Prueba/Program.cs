using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;

// --- INICIO DE LA CONFIGURACIÓN ---
var builder = WebApplication.CreateBuilder(args);

// 2. Lee la configuración de appsettings.json
builder.Services.Configure<UniversityDatabaseSettings>(
    builder.Configuration.GetSection("UniversityDatabaseSettings"));

// 3. Registra el "cliente" de MongoDB
builder.Services.AddSingleton<IMongoClient>(s =>
    new MongoClient(s.GetRequiredService<IOptions<UniversityDatabaseSettings>>().Value.ConnectionString)
);

// --- ¡NUEVO! Registra HttpClient ---
builder.Services.AddHttpClient();

// 4. Registra los servicios de tu API (Swagger, etc.)
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- ¡NUEVO! Configura CORS ---
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:8080", "http://localhost:9000")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});


var app = builder.Build();

// Configura el pipeline de HTTP (Swagger, etc.)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

// --- 5. TU ENDPOINT DE PRUEBA (Lo mantenemos) ---
app.MapGet("/test-mongo-connection", async (IMongoClient mongoClient, IOptions<UniversityDatabaseSettings> settings) =>
{
    try
    {
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        var collection = db.GetCollection<Estudiante>(settings.Value.StudentsCollectionName);
        var count = await collection.CountDocumentsAsync(FilterDefinition<Estudiante>.Empty);
        return Results.Ok($"¡Conexión exitosa! La colección 'estudiantes' tiene {count} documentos.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al conectar con MongoDB: {ex.Message}");
    }
});

// --- 6. ENDPOINT DE LOGIN (CORREGIDO) ---
app.MapPost("/api/auth/login", async (
    [FromBody] LoginRequest login,
    IMongoClient mongoClient,
    IOptions<UniversityDatabaseSettings> settings) =>
{
    var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
    var collection = db.GetCollection<Estudiante>(settings.Value.StudentsCollectionName);

    // --- ¡AQUÍ ESTÁ EL CAMBIO! ---
    // Ahora usamos login.codigoEstudiante y login.password (con minúsculas)
    // para que coincida 100% con la clase LoginRequest y el JSON de Vue.
    var estudiante = await collection.Find(e =>
        e.CodigoEstudiante == login.codigoEstudiante &&
        e.PasswordHash == login.password
    ).FirstOrDefaultAsync();

    if (estudiante == null)
    {
        return Results.Problem(
            detail: "Código de estudiante o contraseña inválidos.",
            statusCode: StatusCodes.Status401Unauthorized
        );
    }
    return Results.Ok(estudiante);
});

// --- 8. ENDPOINT DE CHAT (RAG con Ollama) ---
app.MapPost("/api/chat", async (
    [FromBody] ChatRequest chatRequest,
    IMongoClient mongoClient,
    IOptions<UniversityDatabaseSettings> settings,
    IHttpClientFactory httpClientFactory) =>
{
    try
    {
        // ... (el código del chat se mantiene igual) ...
        var db = mongoClient.GetDatabase(settings.Value.DatabaseName);
        var collection = db.GetCollection<DocumentoGeneral>(settings.Value.GeneralDocumentsCollectionName);
        var documento = await collection.Find(d => d.Tipo == "reglamento").FirstOrDefaultAsync();

        if (documento == null)
        {
            var docEjemplo = new DocumentoGeneral
            {
                Tipo = "reglamento",
                Contenido = "Capítulo 4: Procesos Académicos. Artículo 45: Retiro de Curso. El proceso para el retiro es: 1. Llenar el formulario F-02, disponible en la sección de trámites. 2. Pagar la tasa de 50 soles en tesorería. 3. El plazo máximo es hasta la semana 8 del ciclo académico."
            };
            await collection.InsertOneAsync(docEjemplo);
            documento = docEjemplo;
        }
        string contexto = documento.Contenido;
        string prompt = $@"
            **Instrucción:** Eres un asistente estudiantil amigable. Responde la pregunta del usuario basándote *únicamente* en el siguiente contexto.
            **Contexto (Reglamento):**
            {contexto}
            **Pregunta del Usuario:**
            {chatRequest.Pregunta}
            **Respuesta:**
        ";
        var httpClient = httpClientFactory.CreateClient();
        var ollamaUrl = "http://localhost:11434/api/generate";
        var ollamaRequest = new
        {
            model = "tinyllama",
            prompt = prompt,
            stream = false
        };
        var requestContent = new StringContent(JsonSerializer.Serialize(ollamaRequest), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(ollamaUrl, requestContent);
        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem("Error llamando a la API de Ollama.");
        }
        var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>();
        return Results.Ok(new { respuesta = ollamaResponse?.Response });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error procesando el chat: {ex.Message}. Asegúrate de que Ollama esté corriendo.");
    }
});


app.Run();

// --- MODELOS DE DATOS (Al final del archivo) ---

// --- Modelos de Login (CORREGIDOS) ---
public class LoginRequest
{
    // --- ¡AQUÍ ESTÁ EL CAMBIO! ---
    // Propiedades en minúscula para coincidir 100% con el JSON de Vue/axios
    public string codigoEstudiante { get; set; } = null!;
    public string password { get; set; } = null!;
}

// --- Modelos de Chat ---
public class ChatRequest
{
    public string Pregunta { get; set; } = null!;
}

public class OllamaResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("response")]
    public string Response { get; set; } = null!;
}

// --- Modelo para el documento de RAG ---
public class DocumentoGeneral
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    [BsonElement("tipo")]
    public string Tipo { get; set; } = null!;

    [BsonElement("contenido")]
    public string Contenido { get; set; } = null!;
}

// --- Modelos de Estudiante ---
public class Estudiante
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = null!;

    [BsonElement("codigo_estudiante")]
    public string CodigoEstudiante { get; set; } = null!;

    [BsonElement("password_hash")]
    public string PasswordHash { get; set; } = null!;

    [BsonElement("nombre_completo")]
    public string NombreCompleto { get; set; } = null!;

    [BsonElement("carrera")]
    public string Carrera { get; set; } = null!;

    [BsonElement("promedio_ponderado")]
    public double PromedioPonderado { get; set; }

    [BsonElement("asesor")]
    public Asesor Asesor { get; set; } = null!;

    [BsonElement("cursos_inscritos")]
    public List<Curso> CursosInscritos { get; set; } = null!;
}

public class Asesor
{
    [BsonElement("nombre")]
    public string Nombre { get; set; } = null!;

    [BsonElement("correo")]
    public string Correo { get; set; } = null!;
}

public class Curso
{
    [BsonElement("nombre")]
    public string Nombre { get; set; } = null!;

    [BsonElement("codigo")]
    public string Codigo { get; set; } = null!;
}

// --- Modelo de Configuración ---
public class UniversityDatabaseSettings
{
    public string ConnectionString { get; set; } = null!;
    public string DatabaseName { get; set; } = null!;
    public string StudentsCollectionName { get; set; } = null!;
    public string FormsCollectionName { get; set; } = null!;
    public string ChatMessagesCollectionName { get; set; } = null!;
    public string GeneralDocumentsCollectionName { get; set; } = null!;
}