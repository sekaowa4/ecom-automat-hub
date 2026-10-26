import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({plugins:[react()],build:{outDir:'../server/wwwroot',emptyOutDir:true},server:{proxy:{'/api':{target:'http://127.0.0.1:5188',changeOrigin:true}}}});
