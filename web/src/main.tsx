import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router-dom'
import './index.css'
import App from './App'
import NewLesson from './pages/NewLesson'
import RunView from './pages/RunView'
import LessonView from './pages/LessonView'
import History from './pages/History'

const queryClient = new QueryClient()

const router = createBrowserRouter([
  {
    path: '/',
    element: <App />,
    children: [
      { index: true, element: <NewLesson /> },
      { path: 'run', element: <RunView /> },
      { path: 'lessons/:id', element: <LessonView /> },
      { path: 'history', element: <History /> },
    ],
  },
])

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
