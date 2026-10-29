using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Learning;

public class UserCourseProgressConfiguration : IEntityTypeConfiguration<UserCourseProgress>
{
    public void Configure(EntityTypeBuilder<UserCourseProgress> builder)
    {
        builder.ToTable("user_course_progress", Schemas.Learning);

        builder.HasKey(p => new { p.UserId, p.CourseId });
    }
}
