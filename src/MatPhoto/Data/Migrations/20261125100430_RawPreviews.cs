using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatPhoto.Data.Migrations
{
    /// <summary>
    /// The camera RAW files are read anew: before, ImageMagick took most of them for a TIFF (a NEF showed its tiny first picture, an ARW none). Pending again, the next
    /// scan makes their thumbnails from the picture the camera embedded (they stay visible meanwhile).
    /// </summary>
    public partial class RawPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Media" SET "ThumbState" = 0, "ThumbError" = NULL
                WHERE lower("Path") ~ '\.(dng|cr2|cr3|nef|arw|orf|rw2|raf|pef|srw)$';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
