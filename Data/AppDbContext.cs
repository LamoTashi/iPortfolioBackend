using Microsoft.EntityFrameworkCore;
using iPortfolioBackend.Models;

namespace iPortfolioBackend.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<Projects> Projects { get; set; }
        public DbSet<ContactMessage> ContactMessages { get; set; }

    }
}