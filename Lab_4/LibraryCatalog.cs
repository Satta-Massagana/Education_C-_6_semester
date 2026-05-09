using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Lab4;

public sealed class LibraryCatalog : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(
        LockRecursionPolicy.NoRecursion
    );
    private readonly List<Book> _books = new List<Book>();

    public sealed class Book
    {
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
    }

    public void AddBook(string title, string author)
    {
        _lock.EnterWriteLock();
        try
        {
            _books.Add(new Book { Title = title, Author = author });
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool RemoveBook(string title)
    {
        _lock.EnterWriteLock();
        try
        {
            Book? book = _books.FirstOrDefault(b =>
                string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase)
            );
            if (book is null)
            {
                return false;
            }

            return _books.Remove(book);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool UpdateBook(string title, string newTitle, string newAuthor)
    {
        _lock.EnterWriteLock();
        try
        {
            Book? book = _books.FirstOrDefault(b =>
                string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase)
            );
            if (book is null)
            {
                return false;
            }

            book.Title = newTitle;
            book.Author = newAuthor;
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public List<Book> SearchBooks(string keyword)
    {
        _lock.EnterReadLock();
        try
        {
            return _books
                .Where(b =>
                    b.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || b.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )
                .Select(b => new Book { Title = b.Title, Author = b.Author })
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public List<Book> GetAllBooks()
    {
        _lock.EnterReadLock();
        try
        {
            return _books.Select(b => new Book { Title = b.Title, Author = b.Author }).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryAddBook(string title, string author, int timeoutMs)
    {
        if (!_lock.TryEnterWriteLock(timeoutMs))
        {
            return false;
        }

        try
        {
            _books.Add(new Book { Title = title, Author = author });
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool TrySearchBooks(string keyword, int timeoutMs)
    {
        return TrySearchBooks(keyword, timeoutMs, out _);
    }

    public bool TrySearchBooks(string keyword, int timeoutMs, out List<Book> results)
    {
        results = new List<Book>();
        if (!_lock.TryEnterReadLock(timeoutMs))
        {
            return false;
        }

        try
        {
            results = _books
                .Where(b =>
                    b.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || b.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )
                .Select(b => new Book { Title = b.Title, Author = b.Author })
                .ToList();
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void SimulateWriteLock(int holdMs)
    {
        _lock.EnterWriteLock();
        try
        {
            Thread.Sleep(holdMs);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
