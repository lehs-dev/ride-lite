IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [Permissions] (
    [Id] uniqueidentifier NOT NULL,
    [Code] nvarchar(100) NOT NULL,
    [Description] nvarchar(250) NOT NULL,
    [Module] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_Permissions] PRIMARY KEY ([Id])
);

CREATE TABLE [Roles] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(50) NOT NULL,
    [Description] nvarchar(250) NULL,
    [IsSystemRole] bit NOT NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
);

CREATE TABLE [Users] (
    [Id] uniqueidentifier NOT NULL,
    [Username] nvarchar(100) NOT NULL,
    [PasswordHash] nvarchar(256) NOT NULL,
    [PasswordSalt] nvarchar(128) NOT NULL,
    [FullName] nvarchar(200) NOT NULL,
    [Email] nvarchar(256) NOT NULL,
    [PhoneNumber] nvarchar(max) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);

CREATE TABLE [RolePermissions] (
    [RoleId] uniqueidentifier NOT NULL,
    [PermissionId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_RolePermissions] PRIMARY KEY ([RoleId], [PermissionId]),
    CONSTRAINT [FK_RolePermissions_Permissions_PermissionId] FOREIGN KEY ([PermissionId]) REFERENCES [Permissions] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_RolePermissions_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Trips] (
    [id] uniqueidentifier NOT NULL,
    [riderId] uniqueidentifier NOT NULL,
    [driverId] uniqueidentifier NULL,
    [status] nvarchar(255) NOT NULL,
    [fromLat] float NOT NULL,
    [fromLon] float NOT NULL,
    [fromAddress] nvarchar(255) NOT NULL,
    [toLat] float NOT NULL,
    [toLon] float NOT NULL,
    [toAddress] nvarchar(255) NOT NULL,
    [distance] float NULL,
    [estimatedTime] float NULL,
    [price] decimal(10,2) NULL,
    [createdAt] datetime2 NOT NULL,
    [updatedAt] datetime2 NOT NULL,
    [pickUpTime] datetime2 NULL,
    [dropOffTime] datetime2 NULL,
    [paymentMethod] nvarchar(255) NOT NULL,
    [paymentStatus] nvarchar(255) NOT NULL,
    CONSTRAINT [PK_Trips] PRIMARY KEY ([id]),
    CONSTRAINT [FK_Trips_Users_driverId] FOREIGN KEY ([driverId]) REFERENCES [Users] ([Id]),
    CONSTRAINT [FK_Trips_Users_riderId] FOREIGN KEY ([riderId]) REFERENCES [Users] ([Id])
);

CREATE TABLE [UserRoles] (
    [UserId] uniqueidentifier NOT NULL,
    [RoleId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_UserRoles_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserRoles_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE UNIQUE INDEX [IX_Permissions_Code] ON [Permissions] ([Code]);

CREATE INDEX [IX_RolePermissions_PermissionId] ON [RolePermissions] ([PermissionId]);

CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);

CREATE INDEX [IX_Trips_driverId] ON [Trips] ([driverId]);

CREATE INDEX [IX_Trips_riderId] ON [Trips] ([riderId]);

CREATE INDEX [IX_UserRoles_RoleId] ON [UserRoles] ([RoleId]);

CREATE UNIQUE INDEX [IX_Users_Email] ON [Users] ([Email]);

CREATE UNIQUE INDEX [IX_Users_Username] ON [Users] ([Username]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261030162825_InitialTripSchema', N'10.0.12');

COMMIT;
GO

