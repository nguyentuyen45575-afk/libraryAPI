using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình Database trong bộ nhớ
builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseInMemoryDatabase("LibraryDb_v2"));

// 2. Cấu hình CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Seed dữ liệu mẫu
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    if (!db.Readers.Any())
    {
        db.Readers.AddRange(
            new Reader { Name = "Nguyễn Văn A", Email = "a@gmail.com", Phone = "0901111111" },
            new Reader { Name = "Trần Thị B", Email = "b@gmail.com", Phone = "0902222222" }
        );
        db.Books.AddRange(
            new Book { Title = "Đắc Nhân Tâm", Author = "Dale Carnegie", Category = "Kỹ năng sống", TotalCopies = 5, AvailableCopies = 5 },
            new Book { Title = "Nhà Giả Kim", Author = "Paulo Coelho", Category = "Tiểu thuyết", TotalCopies = 3, AvailableCopies = 3 },
            new Book { Title = "Atomic Habits", Author = "James Clear", Category = "Kỹ năng sống", TotalCopies = 4, AvailableCopies = 4 },
            new Book { Title = "Doraemon", Author = "Fujiko F. Fujio", Category = "Truyện tranh", TotalCopies = 10, AvailableCopies = 10 }
        );
        db.SaveChanges();
    }
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// ==========================================
// API ENDPOINTS
// ==========================================

// --- 1. QUẢN LÝ SÁCH ---
app.MapGet("/api/books", async (string? search, LibraryDbContext db) => 
{
    var query = db.Books.AsQueryable();
    if (!string.IsNullOrEmpty(search))
    {
        query = query.Where(b => b.Title.Contains(search, StringComparison.OrdinalIgnoreCase) 
                              || b.Author.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || b.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
    return await query.ToListAsync();
});

app.MapPost("/api/books", async (Book book, LibraryDbContext db) =>
{
    book.AvailableCopies = book.TotalCopies;
    db.Books.Add(book);
    await db.SaveChangesAsync();
    return Results.Created($"/api/books/{book.Id}", book);
});

app.MapPut("/api/books/{id}", async (int id, Book updatedBook, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book == null) return Results.NotFound();
    
    // Cập nhật số lượng bản sao nếu thay đổi
    if (updatedBook.TotalCopies != book.TotalCopies)
    {
        int diff = updatedBook.TotalCopies - book.TotalCopies;
        book.AvailableCopies += diff;
    }
    
    book.Title = updatedBook.Title;
    book.Author = updatedBook.Author;
    book.Category = updatedBook.Category;
    book.TotalCopies = updatedBook.TotalCopies;
    
    await db.SaveChangesAsync();
    return Results.Ok(book);
});

app.MapDelete("/api/books/{id}", async (int id, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book == null) return Results.NotFound();
    db.Books.Remove(book);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// --- 2. QUẢN LÝ ĐỘC GIẢ ---
app.MapGet("/api/readers", async (LibraryDbContext db) => await db.Readers.ToListAsync());

app.MapPost("/api/readers", async (Reader reader, LibraryDbContext db) =>
{
    db.Readers.Add(reader);
    await db.SaveChangesAsync();
    return Results.Created($"/api/readers/{reader.Id}", reader);
});

// --- 3. MƯỢN / TRẢ & TÍNH PHẠT ---
app.MapPost("/api/borrows", async (BorrowRequest req, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(req.BookId);
    var reader = await db.Readers.FindAsync(req.ReaderId);
    
    if (book == null || reader == null) return Results.BadRequest("Không tìm thấy sách hoặc độc giả");
    if (book.AvailableCopies <= 0) return Results.BadRequest("Sách đã hết lượt mượn");

    book.AvailableCopies--;
    
    var record = new BorrowRecord 
    { 
        BookId = req.BookId, 
        ReaderId = req.ReaderId, 
        BorrowDate = DateTime.Now,
        DueDate = DateTime.Now.AddDays(7), // Hạn trả 7 ngày
        Status = "Đang mượn"
    };
    
    db.BorrowRecords.Add(record);
    await db.SaveChangesAsync();
    return Results.Ok(record);
});

app.MapPost("/api/returns/{id}", async (int id, LibraryDbContext db) =>
{
    var record = await db.BorrowRecords.FindAsync(id);
    if (record == null || record.Status == "Đã trả") return Results.NotFound();

    record.ReturnDate = DateTime.Now;
    record.Status = "Đã trả";
    
    // Tính phí phạt: 5.000đ / ngày quá hạn
    if (record.ReturnDate > record.DueDate)
    {
        int daysOverdue = (record.ReturnDate.Value - record.DueDate).Days;
        record.FineAmount = daysOverdue * 5000;
    }

    var book = await db.Books.FindAsync(record.BookId);
    if (book != null) book.AvailableCopies++;

    await db.SaveChangesAsync();
    return Results.Ok(record);
});

// --- 4. THỐNG KÊ ---
app.MapGet("/api/stats", async (LibraryDbContext db) =>
{
    var activeBorrows = await db.BorrowRecords.CountAsync(r => r.Status == "Đang mượn");
    
    // Sách phổ biến nhất (dựa trên tổng số lần mượn)
    var popularBooks = await db.BorrowRecords
        .GroupBy(r => r.BookId)
        .Select(g => new { BookId = g.Key, BorrowCount = g.Count() })
        .OrderByDescending(x => x.BorrowCount)
        .Take(3)
        .ToListAsync();

    // Lấy tên sách cho danh sách phổ biến
    var result = new List<object>();
    foreach (var item in popularBooks)
    {
        var book = await db.Books.FindAsync(item.BookId);
        if (book != null) result.Add(new { book.Title, item.BorrowCount });
    }

    return Results.Ok(new { ActiveBorrows = activeBorrows, PopularBooks = result });
});

app.Run();

// ==========================================
// MODELS & DB CONTEXT
// ==========================================

public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }
    public DbSet<Book> Books { get; set; }
    public DbSet<Reader> Readers { get; set; }
    public DbSet<BorrowRecord> BorrowRecords { get; set; }
}

public class Book
{
    public int Id { get; set; }
    [Required] public string Title { get; set; } = "";
    [Required] public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }
}

public class Reader
{
    public int Id { get; set; }
    [Required] public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

public class BorrowRecord
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int ReaderId { get; set; }
    public DateTime BorrowDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? ReturnDate { get; set; }
    public decimal FineAmount { get; set; } = 0;
    public string Status { get; set; } = "Đang mượn";
}

public class BorrowRequest
{
    public int BookId { get; set; }
    public int ReaderId { get; set; }
}
