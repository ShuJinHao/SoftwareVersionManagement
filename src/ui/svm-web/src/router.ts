import { createRouter, createWebHistory } from 'vue-router'
import LoginPage from './pages/LoginPage.vue'
import PasswordPage from './pages/PasswordPage.vue'
import UsersPage from './pages/UsersPage.vue'
import SitePage from './pages/SitePage.vue'
import SoftwarePage from './pages/SoftwarePage.vue'
import InstancesPage from './pages/InstancesPage.vue'
import EnrollmentPage from './pages/EnrollmentPage.vue'
import ReleasesPage from './pages/ReleasesPage.vue'

export const router = createRouter({ history: createWebHistory(), routes: [
  { path: '/', redirect: '/users' }, { path: '/login', component: LoginPage },
  { path: '/password', component: PasswordPage }, { path: '/users', component: UsersPage },
  { path: '/instances', component: InstancesPage }, { path: '/enrollment', component: EnrollmentPage },
  { path: '/releases', component: ReleasesPage },
  { path: '/site', component: SitePage }, { path: '/software', component: SoftwarePage },
  { path: '/:pathMatch(.*)*', redirect: '/users' },
] })
