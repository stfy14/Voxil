using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.Collections.Generic;
using System.IO; // <--- Не забудь добавить System.IO
using System.Runtime.CompilerServices;

public class ImGuiController : IDisposable
{
    private bool _frameBegun;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _vertexBufferSize;
    private int _indexBuffer;
    private int _indexBufferSize;
    private int _fontTexture;
    private int _shader;
    private int _shaderFontTextureLocation;
    private int _shaderProjectionMatrixLocation;
    private int _windowWidth;
    private int _windowHeight;
    private System.Numerics.Vector2 _scaleFactor = System.Numerics.Vector2.One;

    public ImGuiController(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;

        IntPtr context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        var io = ImGui.GetIO();
        
        // --- ЗАГРУЗКА ШРИФТА ---
        string fontPath = "Assets/RobotoMono-Regular.ttf";
        float fontSize = 16.0f; // Размер шрифта

        if (File.Exists(fontPath))
        {
            // Загружаем шрифт с поддержкой КИРИЛЛИЦЫ
            // null в конфиге, а третий аргумент - диапазоны символов
            io.Fonts.AddFontFromFileTTF(fontPath, fontSize, null, io.Fonts.GetGlyphRangesCyrillic());
            Console.WriteLine($"[UI] Custom font loaded: {fontPath}");
        }
        else
        {
            Console.WriteLine($"[UI] Font not found at {fontPath}. Using default.");
            io.Fonts.AddFontDefault();
        }
        // -----------------------

        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceResources();
        SetPerFrameImGuiData(1f / 60f);
        ImGui.NewFrame();
        _frameBegun = true;
    }

    public void WindowResized(int width, int height) { _windowWidth = width; _windowHeight = height; }
    public void DestroyDeviceObjects() { Dispose(); }

    public void Update(GameWindow wnd, float dt)
    {
        if (_frameBegun) ImGui.Render();
        SetPerFrameImGuiData(dt);
        UpdateImGuiInput(wnd);
        _frameBegun = true;
        ImGui.NewFrame();
    }

    public void Render()
    {
        if (_frameBegun) { _frameBegun = false; ImGui.Render(); RenderImDrawData(ImGui.GetDrawData()); }
    }

    private void SetPerFrameImGuiData(float dt)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = _scaleFactor;
        io.DeltaTime = dt; 
    }

    private void UpdateImGuiInput(GameWindow wnd)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        MouseState mouse = wnd.MouseState;
        KeyboardState keyboard = wnd.KeyboardState;

        io.AddMouseButtonEvent(0, mouse[MouseButton.Left]);
        io.AddMouseButtonEvent(1, mouse[MouseButton.Right]);
        io.AddMouseButtonEvent(2, mouse[MouseButton.Middle]);
        io.MousePos = new System.Numerics.Vector2(mouse.X, mouse.Y);
        io.AddMouseWheelEvent(mouse.Scroll.X, mouse.Scroll.Y); 

        foreach (Keys key in Enum.GetValues(typeof(Keys)))
        {
            if (key == Keys.Unknown) continue;
            ImGuiKey imKey = TranslateKey(key);
            if (imKey != ImGuiKey.None)
            {
                io.AddKeyEvent(imKey, keyboard.IsKeyDown(key));
            }
        }
        
        // Ввод символов (для текстовых полей)
        // Чтобы это работало идеально, нужно подписаться на событие TextInput в Game.cs
        // Но для меню пока хватит и этого
    }

    private static ImGuiKey TranslateKey(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9) return ImGuiKey._0 + (key - Keys.D0);
        if (key >= Keys.A && key <= Keys.Z) return ImGuiKey.A + (key - Keys.A);
        if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9) return ImGuiKey.Keypad0 + (key - Keys.KeyPad0);
        if (key >= Keys.F1 && key <= Keys.F12) return ImGuiKey.F1 + (key - Keys.F1);

        switch (key)
        {
            case Keys.Tab: return ImGuiKey.Tab;
            case Keys.Left: return ImGuiKey.LeftArrow;
            case Keys.Right: return ImGuiKey.RightArrow;
            case Keys.Up: return ImGuiKey.UpArrow;
            case Keys.Down: return ImGuiKey.DownArrow;
            case Keys.PageUp: return ImGuiKey.PageUp;
            case Keys.PageDown: return ImGuiKey.PageDown;
            case Keys.Home: return ImGuiKey.Home;
            case Keys.End: return ImGuiKey.End;
            case Keys.Insert: return ImGuiKey.Insert;
            case Keys.Delete: return ImGuiKey.Delete;
            case Keys.Backspace: return ImGuiKey.Backspace;
            case Keys.Space: return ImGuiKey.Space;
            case Keys.Enter: return ImGuiKey.Enter;
            case Keys.Escape: return ImGuiKey.Escape;
            case Keys.LeftControl: return ImGuiKey.LeftCtrl;
            case Keys.RightControl: return ImGuiKey.RightCtrl;
            case Keys.LeftShift: return ImGuiKey.LeftShift;
            case Keys.RightShift: return ImGuiKey.RightShift;
            case Keys.LeftAlt: return ImGuiKey.LeftAlt;
            case Keys.RightAlt: return ImGuiKey.RightAlt;
            case Keys.LeftSuper: return ImGuiKey.LeftSuper;
            case Keys.RightSuper: return ImGuiKey.RightSuper;
            case Keys.Menu: return ImGuiKey.Menu;
            case Keys.Comma: return ImGuiKey.Comma;
            case Keys.Period: return ImGuiKey.Period;
            case Keys.Slash: return ImGuiKey.Slash;
            case Keys.Backslash: return ImGuiKey.Backslash;
            case Keys.Semicolon: return ImGuiKey.Semicolon;
            case Keys.Apostrophe: return ImGuiKey.Apostrophe;
            case Keys.LeftBracket: return ImGuiKey.LeftBracket;
            case Keys.RightBracket: return ImGuiKey.RightBracket;
            case Keys.Minus: return ImGuiKey.Minus;
            case Keys.Equal: return ImGuiKey.Equal;
            default: return ImGuiKey.None;
        }
    }

    private unsafe void RenderImDrawData(ImDrawDataPtr draw_data)
    {
        if (draw_data.CmdListsCount == 0) return;

        // Восстанавливаем Viewport на весь экран перед отрисовкой UI
        GL.Viewport(0, 0, _windowWidth, _windowHeight);

        GL.Enable(EnableCap.Blend);
        GL.Enable(EnableCap.ScissorTest);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        GL.UseProgram(_shader);
        GL.Uniform1(_shaderFontTextureLocation, 0);
        
        var projection = Matrix4.CreateOrthographicOffCenter(0, _windowWidth, _windowHeight, 0, -1f, 1f);
        GL.UniformMatrix4(_shaderProjectionMatrixLocation, false, ref projection);

        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);

        for (int i = 0; i < draw_data.CmdListsCount; i++)
        {
            ImDrawListPtr cmd_list = new ImDrawListPtr(((IntPtr*)draw_data.CmdLists.Data)[i]);

            int vertexSize = cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
            if (vertexSize > _vertexBufferSize)
            {
                int newSize = (int)Math.Max(_vertexBufferSize * 1.5f, vertexSize);
                GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                _vertexBufferSize = newSize;
            }
            int indexSize = cmd_list.IdxBuffer.Size * sizeof(ushort);
            if (indexSize > _indexBufferSize)
            {
                int newSize = (int)Math.Max(_indexBufferSize * 1.5f, indexSize);
                GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                _indexBufferSize = newSize;
            }
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexSize, cmd_list.VtxBuffer.Data);
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexSize, cmd_list.IdxBuffer.Data);

            for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
            {
                ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero) throw new NotImplementedException();

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                
                var clip = pcmd.ClipRect;
                GL.Scissor((int)clip.X, _windowHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));
                
                GL.DrawElements(PrimitiveType.Triangles, (int)pcmd.ElemCount, DrawElementsType.UnsignedShort, (IntPtr)(pcmd.IdxOffset * sizeof(ushort)));
            }
        }
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
    }

    private void CreateDeviceResources()
    {
        _vertexBufferSize = 10000; _indexBufferSize = 2000;
        _vertexArray = GL.GenVertexArray(); _vertexBuffer = GL.GenBuffer(); _indexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 8);
        GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, Unsafe.SizeOf<ImDrawVert>(), 16);

        string vs = "#version 330 core\nlayout(location=0) in vec2 aPos; layout(location=1) in vec2 aUV; layout(location=2) in vec4 aColor; uniform mat4 ProjMtx; out vec2 Frag_UV; out vec4 Frag_Color; void main() { Frag_UV = aUV; Frag_Color = aColor; gl_Position = ProjMtx * vec4(aPos.xy,0,1); }";
        string fs = "#version 330 core\nin vec2 Frag_UV; in vec4 Frag_Color; uniform sampler2D Texture; out vec4 Out_Color; void main() { Out_Color = Frag_Color * texture(Texture, Frag_UV.st); }";
        _shader = CreateShader(vs, fs);
        _shaderProjectionMatrixLocation = GL.GetUniformLocation(_shader, "ProjMtx");
        _shaderFontTextureLocation = GL.GetUniformLocation(_shader, "Texture");

        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bpp);
        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Linear);
        ImGui.GetIO().Fonts.SetTexID((IntPtr)_fontTexture);
    }

    private int CreateShader(string vs, string fs)
    {
        int p = GL.CreateProgram();
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); GL.AttachShader(p, v);
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); GL.AttachShader(p, f);
        GL.LinkProgram(p); GL.DeleteShader(v); GL.DeleteShader(f); return p;
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vertexArray); GL.DeleteBuffer(_vertexBuffer); GL.DeleteBuffer(_indexBuffer);
        GL.DeleteTexture(_fontTexture); GL.DeleteProgram(_shader);
    }
}