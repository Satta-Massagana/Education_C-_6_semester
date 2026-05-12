using System.Collections.Concurrent;

namespace Lab5.ConcurrentCollections;

public sealed class Book
{
    public required string Title { get; init; }
    public required string Author { get; init; }
}

public sealed class ConcurrentLibraryCatalog
{
    public ConcurrentDictionary<string, Book> Books { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool AddBook(string title, string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        return Books.TryAdd(title, new Book { Title = title, Author = author });
    }

    public bool RemoveBook(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Books.TryRemove(title, out _);
    }

    public bool UpdateBook(string title, string newTitle, string newAuthor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(newAuthor);

        if (!Books.TryGetValue(title, out Book? existingBook))
        {
            return false;
        }

        Book updatedBook = new() { Title = newTitle, Author = newAuthor };

        if (!string.Equals(title, newTitle, StringComparison.OrdinalIgnoreCase))
        {
            if (!Books.TryAdd(newTitle, updatedBook))
            {
                return false;
            }

            if (!Books.TryRemove(title, out _))
            {
                Books.TryRemove(newTitle, out _);
                return false;
            }

            return true;
        }

        return Books.TryUpdate(title, updatedBook, existingBook);
    }

    public IReadOnlyList<Book> SearchBooks(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return GetAllBooks();
        }

        return Books
            .Values.Where(book =>
                book.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || book.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
    }

    public IReadOnlyList<Book> GetAllBooks()
    {
        return Books.Values.ToList();
    }

    public int GetBookCount()
    {
        return Books.Count;
    }

    public void ClearCatalog()
    {
        Books.Clear();
    }

    public bool TryGetBook(string title, out Book book)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (Books.TryGetValue(title, out Book? foundBook))
        {
            book = foundBook;
            return true;
        }

        book = null!;
        return false;
    }

    public Book GetOrAddBook(string title, string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        return Books.GetOrAdd(title, key => new Book { Title = key, Author = author });
    }
}
