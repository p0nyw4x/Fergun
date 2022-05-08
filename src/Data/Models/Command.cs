﻿using System.ComponentModel.DataAnnotations;

namespace Fergun.Data.Models;

/// <summary>
/// Represents a bot command.
/// </summary>
public class Command
{
    /// <summary>
    /// Gets or sets the name of this command.
    /// </summary>
    [Key]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the usage count.
    /// </summary>
    [Required]
    public int UsageCount { get; set; }
}