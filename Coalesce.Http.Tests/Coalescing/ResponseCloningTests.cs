using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Coalesce.Http.Coalesce.Http.Coalescing;
using Coalesce.Http.Coalesce.Http.Options;
using FluentAssertions;

namespace Coalesce.Http.Tests.Coalescing;

public class ResponseCloningTests
{
    /// <summary>
    /// Verifica que múltiples callers concurrentes pueden leer el contenido de la respuesta
    /// de forma independiente sin problemas de streaming.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_EachCallerCanReadContent()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var expectedContent = "Test response content";
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act - Iniciar múltiples llamadas concurrentes
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);
        var task3 = coalescer.ExecuteAsync(key, Factory);

        // Completar la request original
        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent, Encoding.UTF8, "text/plain")
        };
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;
        var response3 = await task3;

        // Leer el contenido de cada respuesta independientemente
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        var content3 = await response3.Content.ReadAsStringAsync();

        // Assert - Cada caller debe poder leer el contenido completo
        content1.Should().Be(expectedContent);
        content2.Should().Be(expectedContent);
        content3.Should().Be(expectedContent);

        // Las respuestas deben ser objetos diferentes (clonadas)
        response1.Should().NotBeSameAs(response2);
        response2.Should().NotBeSameAs(response3);
        response1.Should().NotBeSameAs(response3);
    }

    /// <summary>
    /// Verifica que cada caller puede hacer Dispose() de su respuesta
    /// sin afectar a los otros callers.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_EachCallerCanDisposeIndependently()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var expectedContent = "Test response content";
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);
        var task3 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent, Encoding.UTF8, "text/plain")
        };
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;
        var response3 = await task3;

        // Leer contenido de response1
        var content1 = await response1.Content.ReadAsStringAsync();
        content1.Should().Be(expectedContent);

        // Dispose de response1
        response1.Dispose();

        // Assert - Las otras respuestas deben seguir funcionando
        var content2 = await response2.Content.ReadAsStringAsync();
        var content3 = await response3.Content.ReadAsStringAsync();

        content2.Should().Be(expectedContent);
        content3.Should().Be(expectedContent);

        // Dispose de las restantes
        response2.Dispose();
        response3.Dispose();
    }

    /// <summary>
    /// Verifica que el clonado funciona correctamente con ReadFromJsonAsync,
    /// un método común al trabajar con APIs REST.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_WorksWithReadFromJsonAsync()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/users/1");
        var expectedUser = new User { Id = 1, Name = "John Doe", Email = "john@example.com" };
        var json = JsonSerializer.Serialize(expectedUser);
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);
        var task3 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;
        var response3 = await task3;

        // Leer y deserializar desde cada respuesta
        var user1 = await response1.Content.ReadFromJsonAsync<User>();
        var user2 = await response2.Content.ReadFromJsonAsync<User>();
        var user3 = await response3.Content.ReadFromJsonAsync<User>();

        // Assert
        user1.Should().BeEquivalentTo(expectedUser);
        user2.Should().BeEquivalentTo(expectedUser);
        user3.Should().BeEquivalentTo(expectedUser);

        // Cleanup
        response1.Dispose();
        response2.Dispose();
        response3.Dispose();
    }

    /// <summary>
    /// Verifica que no hay problemas de streaming cuando se lee el contenido
    /// múltiples veces de forma concurrente y secuencial.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_AvoidStreamingIssues()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var expectedContent = "Large response content that could cause streaming issues";
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent, Encoding.UTF8, "text/plain")
        };
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;

        // Leer el contenido múltiples veces desde la misma respuesta (esto fallaría con un stream compartido)
        var content1a = await response1.Content.ReadAsStringAsync();
        var content1b = await response1.Content.ReadAsStringAsync();
        
        var content2a = await response2.Content.ReadAsStringAsync();
        var content2b = await response2.Content.ReadAsStringAsync();

        // Assert - Cada lectura debe devolver el contenido completo
        content1a.Should().Be(expectedContent);
        content1b.Should().Be(expectedContent);
        content2a.Should().Be(expectedContent);
        content2b.Should().Be(expectedContent);

        // Cleanup
        response1.Dispose();
        response2.Dispose();
    }

    /// <summary>
    /// Verifica que las cabeceras HTTP se copian correctamente en cada clon,
    /// incluyendo las cabeceras del contenido.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_CopiesHeadersCorrectly()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/data");
        var expectedContent = "Test content";
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent, Encoding.UTF8, "application/json")
        };
        originalResponse.Headers.Add("X-Custom-Header", "CustomValue");
        originalResponse.Headers.Add("X-Request-Id", "12345");
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;

        // Assert - Verificar que las cabeceras se copiaron correctamente
        response1.Headers.GetValues("X-Custom-Header").Should().ContainSingle().Which.Should().Be("CustomValue");
        response1.Headers.GetValues("X-Request-Id").Should().ContainSingle().Which.Should().Be("12345");
        
        response2.Headers.GetValues("X-Custom-Header").Should().ContainSingle().Which.Should().Be("CustomValue");
        response2.Headers.GetValues("X-Request-Id").Should().ContainSingle().Which.Should().Be("12345");

        // Verificar cabeceras de contenido
        response1.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response2.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        // Cleanup
        response1.Dispose();
        response2.Dispose();
    }

    /// <summary>
    /// Verifica que el clonado funciona correctamente con contenido binario
    /// (ej. imágenes, archivos).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_WorksWithBinaryContent()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/image.png");
        var expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedBytes)
        };
        originalResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;

        // Leer contenido binario de cada respuesta
        var bytes1 = await response1.Content.ReadAsByteArrayAsync();
        var bytes2 = await response2.Content.ReadAsByteArrayAsync();

        // Assert
        bytes1.Should().Equal(expectedBytes);
        bytes2.Should().Equal(expectedBytes);
        
        response1.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        response2.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        // Cleanup
        response1.Dispose();
        response2.Dispose();
    }

    /// <summary>
    /// Verifica que el clonado funciona con respuestas sin contenido (ej. 204 No Content).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithConcurrentCalls_WorksWithNoContent()
    {
        // Arrange
        var coalescer = new RequestCoalescer(new CoalescerOptions());
        var key = new RequestKey("GET", "https://api.example.com/status");
        var tcs = new TaskCompletionSource<HttpResponseMessage>();

        Task<HttpResponseMessage> Factory()
        {
            return tcs.Task;
        }

        // Act
        var task1 = coalescer.ExecuteAsync(key, Factory);
        var task2 = coalescer.ExecuteAsync(key, Factory);

        var originalResponse = new HttpResponseMessage(HttpStatusCode.NoContent)
        {
            Content = null // Explícitamente sin contenido
        };
        tcs.SetResult(originalResponse);

        var response1 = await task1;
        var response2 = await task2;

        // Assert
        response1.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response2.StatusCode.Should().Be(HttpStatusCode.NoContent);
        
        // Si la respuesta original no tenía contenido, los clones tampoco deberían tenerlo
        if (originalResponse.Content == null)
        {
            response1.Content.Should().BeNull();
            response2.Content.Should().BeNull();
        }

        // Cleanup
        response1.Dispose();
        response2.Dispose();
    }

    // Clase auxiliar para los tests
    private class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
